-- SystemBoost member accounts: security audit 2026-11-12.
-- NEEDS OWNER APPROVAL to apply in Supabase. The audit did not run it anywhere but a local test database (PGlite).
--
-- For a database made from supabase/accounts.sql as it was up to commit af5b464 (v0.8.11). Running the whole of
-- supabase/accounts.sql again does the same; this file holds only what changed. It can run more than once.
-- Members, password hashes, recovery codes and sessions are left as they are: nobody is logged out, nothing is re-hashed.
--
-- What it changes
--   1. Every account_ function gets a fixed search_path (Supabase's linter: function_search_path_mutable).
--   2. A wrong password on "change password" or "leave" (with a session) now counts like a wrong login: 20 an hour
--      per address, 5 then a 15-minute lock per account. Before, a session could guess, and run bcrypt, without end.
--   3. A login with an id that does not exist runs bcrypt too, so the reply time no longer tells which ids exist.
--   4. "This id is taken" at sign-up: 20 an hour per address, then "busy" (it also tells which ids exist).
--   5. The check at the end covers every table and every account_ function (before: accounts and account_call).
--   6. A member id that was deleted (the member left, or the seller deleted it) cannot be signed up again: a new table
--      account_gone keeps a one-way value of it made with the Worker's pepper (privacy review L7). Only a Worker that
--      sends that value ("gone") gets this; an older one signs up and deletes as before.
--   The rights themselves are re-stated unchanged: RLS on with no policy, nothing for public/anon/authenticated,
--   execute for service_role only. The audit found them already so in the repository's SQL; the live project is
--   not checked (see the query at the very end).
--
-- Order: this file first, then deploy the Worker (src/accounts.js sends the address for 2 and the limit for 4).
-- Either order works: an old Worker with this SQL gets the account lock in 2 without the per-address count, and a
-- new Worker with the old SQL behaves as before.
-- Back: run supabase/accounts.sql from commit af5b464 again (it replaces the functions; the data is untouched).

begin;

-- the discount-ticket columns came with v0.8.7; if an early copy of the table lacks them, add them (no-op otherwise)
alter table public.accounts add column if not exists ticket_percent integer;
alter table public.accounts add column if not exists ticket_expires timestamptz;
alter table public.accounts add column if not exists ticket_order text;
alter table public.accounts add column if not exists ticket_used timestamptz;

-- 6. ids of members who left, as a one-way value (as in supabase/accounts.sql)
create table if not exists public.account_gone (
  h text primary key,
  at timestamptz not null default now()
);

-- 1. helpers whose bodies already name every schema: only the search_path changes
alter function public.account_fail(integer, text) set search_path = '';
alter function public.account_ms(timestamptz) set search_path = '';
alter function public.account_hour() set search_path = '';
alter function public.account_over(text, integer) set search_path = '';
alter function public.account_count(text) set search_path = '';
alter function public.account_session(text, text, jsonb) set search_path = '';
alter function public.account_member(text) set search_path = '';

-- 2. a miss without an address (an old Worker on "change password") still locks the account
-- A wrong password or recovery code: counted for the address and the account. Five lock the account.
create or replace function public.account_missed(a public.accounts, b jsonb) returns jsonb
language plpgsql set search_path = '' as $$
declare
  lock boolean := a.fails + 1 >= (b->>'loginTries')::integer;
begin
  if b->>'ip' is not null then perform public.account_count('fail:' || (b->>'ip')); end if;
  update public.accounts set fails = case when lock then 0 else a.fails + 1 end,
    locked_until = case when lock then now() + make_interval(mins => (b->>'lockMin')::integer) end
    where id = a.id;
  return public.account_fail(401, 'wrong');
end $$;

-- 2, 3, 4, 6. the one entry point, a copy of the one in supabase/accounts.sql. Each call is one transaction.
create or replace function public.account_call(action text, b jsonb) returns jsonb
language plpgsql set search_path = public, extensions as $$
declare
  a public.accounts;
  inviter public.accounts;
  invite text := coalesce(b->>'invite', '');
  add_days integer;
  q text := lower(trim(coalesce(b->>'q', '')));
  week timestamptz := now() - interval '7 days';
  members jsonb;
  summary jsonb;
begin
  -- housekeeping from the Worker's hourly job, which also keeps a free project from counting as idle
  if action = 'ping' then
    delete from account_hits where hour < account_hour();
    delete from account_sessions where expires < now();
    return jsonb_build_object('ok', true);
  end if;

  if action = 'code' then
    return jsonb_build_object('member', exists (select 1 from accounts where invite_code = b->>'code'));
  end if;

  if action = 'signup' then
    if account_over('signup:' || (b->>'ip'), (b->>'signupsPerIp')::integer)
       or account_over('signup:all', (b->>'signupsPerHour')::integer)
       or account_over('taken:' || (b->>'ip'), (b->>'takenPerIp')::integer) then
      return account_fail(429, 'busy');
    end if;
    -- "taken" tells whether an id exists, so one address hears it a limited number of times an hour
    if exists (select 1 from accounts where id = b->>'id') then
      perform account_count('taken:' || (b->>'ip'));
      return account_fail(409, 'taken');
    end if;
    -- an id whose member left is not handed to somebody else (an older Worker sends no "gone": not checked then)
    if b->>'gone' is not null and exists (select 1 from account_gone where h = b->>'gone') then
      perform account_count('taken:' || (b->>'ip'));
      return account_fail(409, 'taken');
    end if;
    if invite <> '' then
      select * into inviter from accounts where invite_code = invite and not blocked for update;
      if inviter.id is null then return account_fail(404, 'unknown'); end if;
      if inviter.invites >= (b->>'inviteMax')::integer then return account_fail(409, 'used up'); end if;
    end if;
    perform account_count('signup:' || (b->>'ip'));
    perform account_count('signup:all');
    insert into accounts (id, hash, recovery, last_login, consent, invited_by, ticket_percent, ticket_expires)
      values (b->>'id', crypt(b->>'pw', gen_salt('bf', 10)), b->>'proof', now(), b->>'consent', inviter.id,
              case when (b->>'ticket')::boolean then (b->>'ticketPercent')::integer end,
              case when (b->>'ticket')::boolean then now() + make_interval(days => (b->>'ticketDays')::integer) end)
      on conflict (id) do nothing;
    if not found then return account_fail(409, 'taken'); end if;
    if inviter.id is not null then
      add_days := least((b->>'rewardDays')::integer, greatest(0, (b->>'rewardCap')::integer - inviter.earned_days));
      update accounts set invites = invites + 1, reward_days = reward_days + add_days,
        earned_days = earned_days + add_days where id = inviter.id;
    end if;
    perform account_session(b->>'id', b->>'session', b);
    return jsonb_build_object('id', b->>'id');
  end if;

  if action = 'login' or action = 'recover' then
    if account_over('fail:' || (b->>'ip'), (b->>'failsPerIp')::integer) then return account_fail(429, 'busy'); end if;
    select * into a from accounts where id = b->>'id' for update;
    if a.id is null then
      -- the same bcrypt work as a wrong password, so the time taken does not tell which ids exist
      if action = 'login' then perform crypt(b->>'pw', gen_salt('bf', 10)); end if;
      perform account_count('fail:' || (b->>'ip'));
      return account_fail(401, 'wrong');
    end if;
    if a.locked_until > now() then return account_fail(429, 'locked'); end if;
    if action = 'login' then
      if a.hash <> crypt(b->>'pw', a.hash) then return account_missed(a, b); end if;
    elsif a.recovery <> b->>'proof' then
      return account_missed(a, b);
    end if;
    if a.blocked then return account_fail(403, 'blocked'); end if;
    if action = 'login' then
      update accounts set fails = 0, last_login = now() where id = a.id;
      perform account_session(a.id, b->>'session', b);
    else
      perform account_set_password(a.id, b);
    end if;
    return jsonb_build_object('id', a.id);
  end if;

  if action = 'admin/list' then
    select coalesce(jsonb_agg(m), '[]'::jsonb) into members from (
      select id, account_ms(created) as created, account_ms(last_login) as "lastLogin", invite_code as code,
             invited_by as "invitedBy", invites, earned_days as earned, reward_days as waiting, blocked::integer as blocked
      from accounts
      where q = '' or position(q in id) > 0 or position(q in lower(coalesce(invite_code, ''))) > 0
      order by accounts.created desc limit 200) m;
    select jsonb_build_object('total', count(*), 'week', count(*) filter (where created > week),
             'active', count(*) filter (where last_login > week), 'blocked', count(*) filter (where blocked),
             'invited', count(*) filter (where invited_by is not null)) into summary from accounts;
    return jsonb_build_object('members', members, 'summary', summary);
  end if;

  if action = 'admin/block' or action = 'admin/recovery' or action = 'admin/delete' then
    if not exists (select 1 from accounts where id = b->>'id') then return account_fail(404, '없는 회원이에요'); end if;
    if action = 'admin/block' then
      update accounts set blocked = (b->>'on')::boolean where id = b->>'id';
      if (b->>'on')::boolean then delete from account_sessions where account = b->>'id'; end if;
    elsif action = 'admin/recovery' then
      update accounts set recovery = b->>'proof', fails = 0, locked_until = null where id = b->>'id';
    else
      delete from accounts where id = b->>'id';
      if b->>'gone' is not null then insert into account_gone (h) values (b->>'gone') on conflict (h) do nothing; end if;
    end if;
    return jsonb_build_object('ok', true);
  end if;

  -- a ticket is spent when the seller approves the order, and comes back if the order is refused
  if action = 'ticket/used' then
    update accounts set ticket_used = now() where ticket_order = b->>'order';
    return jsonb_build_object('ok', true);
  end if;
  if action = 'ticket/release' then
    update accounts set ticket_order = null where ticket_order = b->>'order' and ticket_used is null;
    return jsonb_build_object('ok', true);
  end if;

  -- everything below needs a session
  select * into a from account_member(b->>'token');
  if a.id is null then return account_fail(401, 'session'); end if;

  if action = 'me' then
    return jsonb_build_object('id', a.id, 'created', account_ms(a.created), 'code', a.invite_code, 'invites', a.invites,
                              'rewardDays', a.reward_days, 'earnedDays', a.earned_days,
                              'ticket', case when a.ticket_percent is not null and a.ticket_used is null and a.ticket_expires > now()
                                             then jsonb_build_object('percent', a.ticket_percent, 'expires', account_ms(a.ticket_expires)) end);
  end if;

  if action = 'ticket' then
    if a.ticket_percent is null or a.ticket_used is not null or a.ticket_expires <= now() then
      return account_fail(409, 'ticket');
    end if;
    update accounts set ticket_order = b->>'order' where id = a.id;
    return jsonb_build_object('percent', a.ticket_percent);
  end if;

  if action = 'invite' then
    begin
      update accounts set invite_code = b->>'code' where id = a.id;
    exception when unique_violation then
      return account_fail(409, 'retry');
    end;
    return jsonb_build_object('code', b->>'code');
  end if;

  if action = 'reward' then
    select * into a from accounts where id = a.id for update;
    if a.reward_days < 1 then return account_fail(409, 'nothing'); end if;
    update accounts set reward_days = 0 where id = a.id;
    return jsonb_build_object('id', a.id, 'days', a.reward_days);
  end if;

  if action = 'logout' then
    delete from account_sessions where token = b->>'token';
    return jsonb_build_object('ok', true);
  end if;

  -- the password again: the same limits and lock as a login, so a session cannot guess it (or run bcrypt) without end
  if action = 'password' or action = 'delete' then
    if account_over('fail:' || (b->>'ip'), (b->>'failsPerIp')::integer) then return account_fail(429, 'busy'); end if;
    select * into a from accounts where id = a.id for update;
    if a.locked_until > now() then return account_fail(429, 'locked'); end if;
    if a.hash <> crypt(b->>'pw', a.hash) then return account_missed(a, b); end if;
    if action = 'password' then
      perform account_set_password(a.id, b);
      return jsonb_build_object('id', a.id);
    end if;
    delete from accounts where id = a.id;
    if b->>'gone' is not null then insert into account_gone (h) values (b->>'gone') on conflict (h) do nothing; end if;
    return jsonb_build_object('ok', true);
  end if;

  return account_fail(404, 'not found');
end $$;

-- Who can reach what: the Worker's role, and nobody else.
alter table public.accounts enable row level security;
alter table public.account_sessions enable row level security;
alter table public.account_hits enable row level security;
alter table public.account_gone enable row level security;
revoke all on public.accounts, public.account_sessions, public.account_hits, public.account_gone from public, anon, authenticated;
grant select, insert, update, delete on public.accounts, public.account_sessions, public.account_hits, public.account_gone to service_role;
grant usage on schema extensions to service_role;

do $$
declare
  f regprocedure;
begin
  for f in select p.oid::regprocedure from pg_proc p join pg_namespace n on n.oid = p.pronamespace
           where n.nspname = 'public' and p.proname like 'account\_%' loop
    execute format('revoke all on function %s from public, anon, authenticated', f);
    execute format('grant execute on function %s to service_role', f);
  end loop;
end $$;

-- Checked for every table and every account_ function, not only the main ones: anon (the publishable key) and
-- authenticated (anyone signed in to Supabase Auth with it) reach nothing, RLS is on with no policy, and no
-- function runs as its owner.
do $$
declare
  t regclass;
  f regprocedure;
begin
  foreach t in array array['public.accounts', 'public.account_sessions', 'public.account_hits', 'public.account_gone']::regclass[] loop
    if has_table_privilege('anon', t, 'select,insert,update,delete,truncate,references,trigger')
       or has_table_privilege('authenticated', t, 'select,insert,update,delete,truncate,references,trigger')
       or not (select relrowsecurity from pg_class where oid = t)
       or exists (select 1 from pg_policy where polrelid = t) then
      raise exception 'SystemBoost: access rights on % are not as expected, stop here', t;
    end if;
  end loop;
  for f in select p.oid::regprocedure from pg_proc p join pg_namespace n on n.oid = p.pronamespace
           where n.nspname = 'public' and p.proname like 'account\_%' loop
    if has_function_privilege('public', f, 'execute')
       or has_function_privilege('anon', f, 'execute')
       or has_function_privilege('authenticated', f, 'execute')
       or not has_function_privilege('service_role', f, 'execute')
       or (select prosecdef from pg_proc where oid = f) then
      raise exception 'SystemBoost: access rights on % are not as expected, stop here', f;
    end if;
  end loop;
end $$;

commit;

notify pgrst, 'reload schema';
select 'SystemBoost member database: security update applied' as result;

-- To see the live rights without changing anything (SQL Editor), every row should read false, false, true:
--   select p.oid::regprocedure, has_function_privilege('anon', p.oid, 'execute') as anon,
--          has_function_privilege('authenticated', p.oid, 'execute') as authenticated,
--          has_function_privilege('service_role', p.oid, 'execute') as service_role
--   from pg_proc p join pg_namespace n on n.oid = p.pronamespace where n.nspname = 'public' and p.proname like 'account\_%';
