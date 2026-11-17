-- SystemBoost member accounts v2: email accounts, password reset by mail, 2-step login with backup codes, logged-in
-- devices, linked licence keys, consents, a security log and account recovery requests (2026-11-18).
-- NEEDS OWNER APPROVAL to apply in Supabase. Nobody has run it anywhere but a local test database (PGlite).
--
-- For a database made from supabase/accounts.sql up to commit fa7d6d2, i.e. after supabase/migrations/20261112_lockdown.sql
-- (run that one first if it has not run). Running the whole of supabase/accounts.sql again does the same; this file
-- holds the new tables and columns and every account_ function in full. It can run more than once.
-- Members, password hashes, recovery codes and sessions are left as they are: nobody is logged out, nothing is re-hashed.
-- Existing sessions get a public device id; their device name stays empty (shown as "SystemBoost app").
--
-- What it adds
--   accounts            email (unique, lowercase), email_at, totp, totp_pending, totp_step, fail_at
--   account_sessions    sid, created, last_seen, device_name, device_hash, country
--   account_tokens      mailed links and half-done 2-step logins, one-way values only
--   account_backup      backup codes of the 2-step login, one-way values only
--   account_devices     devices that logged in (one-way value of the app's install id, name, first and last time)
--   account_keys        licence key ids linked to an account
--   account_consents    overclocking notice consents given while logged in
--   account_security_log  logins, failed logins, 2-step and password changes ... with the raw IP; deleted after 30 days
--   account_recovery    recovery requests, decided by the seller after 72 hours; deleted after 90 days
--   account_guard       wrong logins for names nobody has (one-way value), so they lock like real accounts
--   functions           account_log, account_guard_missed, account_guarded, account_device (new); account_session,
--                       account_set_password, account_missed, account_call (changed); the rest restated unchanged
-- The rights are stated for every table, old and new: RLS on with no policy, nothing for public/anon/authenticated,
-- execute for service_role only; the check at the end stops with an error otherwise.
--
-- Order: this file first, then deploy the Worker. A Worker from before accounts v2 keeps working with this SQL (its
-- calls send no device, address or identifier hash; those are then left empty). The new Worker with the old SQL
-- would answer the new actions with "unavailable".
-- Back: run supabase/accounts.sql from commit fa7d6d2 again (it replaces the functions; the new tables and columns
-- stay, unused). To remove them as well, drop the new tables and columns by hand after that.

begin;

-- ---------------------------------------------------------------- accounts v2 (2026-11-18)
-- The same lines as in supabase/accounts.sql, in the same order.
-- A verified email (lowercase; one account each), the time it was verified, the 2-step login secret (AES-GCM, sealed
-- by the Worker with a key made from its pepper; "totp_pending" while it is being set up), the last code step used
-- (a code works once), and when the last wrong password was (misses older than a day are forgotten).
alter table public.accounts add column if not exists email text;
alter table public.accounts add column if not exists email_at timestamptz;
alter table public.accounts add column if not exists totp text;
alter table public.accounts add column if not exists totp_pending text;
alter table public.accounts add column if not exists totp_step bigint not null default 0;
alter table public.accounts add column if not exists fail_at timestamptz;
create unique index if not exists accounts_email on public.accounts (email);
-- A session is a logged-in device: a public id for the device list (never the token), when it started and was last
-- used (at most every 10 minutes), the device's name, a one-way value of its install id, and the country.
alter table public.account_sessions add column if not exists sid text;
alter table public.account_sessions add column if not exists created timestamptz not null default now();
alter table public.account_sessions add column if not exists last_seen timestamptz not null default now();
alter table public.account_sessions add column if not exists device_name text;
alter table public.account_sessions add column if not exists device_hash text;
alter table public.account_sessions add column if not exists country text;
update public.account_sessions set sid = encode(extensions.gen_random_bytes(6), 'hex') where sid is null;
create unique index if not exists account_sessions_sid on public.account_sessions (sid);
-- Links mailed or handed out, as a one-way value of the token: a sign-up waiting for its email (with the bcrypt hash
-- of the chosen password), an email being added, a password reset, a key link, a 2-step login half done, the cancel
-- link of a recovery request. "noop" rows are written where an unknown address must cost the same work.
create table if not exists public.account_tokens (
  token text primary key,
  kind text not null,
  account text references public.accounts (id) on delete cascade,
  email text,
  hash text,
  data jsonb not null default '{}'::jsonb,
  tries integer not null default 0,
  created timestamptz not null default now(),
  expires timestamptz not null
);
create index if not exists account_tokens_account on public.account_tokens (account);
create index if not exists account_tokens_email on public.account_tokens (email);
-- Backup codes of the 2-step login, as a one-way value; a used one is deleted.
create table if not exists public.account_backup (
  account text not null references public.accounts (id) on delete cascade,
  h text not null,
  primary key (account, h)
);
-- Devices that logged in (a one-way value of the app's install id), so a new one can be told by mail.
create table if not exists public.account_devices (
  account text not null references public.accounts (id) on delete cascade,
  h text not null,
  name text not null default '',
  first_seen timestamptz not null default now(),
  last_seen timestamptz not null default now(),
  primary key (account, h)
);
-- Licence keys linked to the account (key ids; the keys themselves stay in Cloudflare KV).
create table if not exists public.account_keys (
  account text not null references public.accounts (id) on delete cascade,
  key_id text not null,
  linked timestamptz not null default now(),
  primary key (account, key_id)
);
-- Consents given in the app while logged in (the overclocking notice): version, GPU, app version, time; withdrawals too.
create table if not exists public.account_consents (
  account text not null references public.accounts (id) on delete cascade,
  kind text not null,
  version text not null,
  gpu text not null default '',
  app text not null default '',
  withdrawn boolean not null default false,
  at timestamptz not null default now()
);
create index if not exists account_consents_account on public.account_consents (account, at);
-- The security log: logins, failed logins, 2-step changes, password changes and so on, with the raw IP address,
-- the country and the device name. The only place a raw address is kept; the hourly job deletes rows after 30 days.
create table if not exists public.account_security_log (
  account text not null references public.accounts (id) on delete cascade,
  at timestamptz not null default now(),
  event text not null,
  ip text not null default '',
  country text not null default '',
  device text not null default ''
);
create index if not exists account_security_log_account on public.account_security_log (account, at);
create index if not exists account_security_log_at on public.account_security_log (at);
-- Account recovery requests (2-step device and backup codes lost): the payment facts the member gave, decided by the
-- seller in the dashboard no earlier than "due" (72 hours). Deleted 90 days after they were made.
create table if not exists public.account_recovery (
  id text primary key,
  account text not null references public.accounts (id) on delete cascade,
  created timestamptz not null default now(),
  due timestamptz not null,
  status text not null default 'open',
  facts jsonb not null default '{}'::jsonb,
  reason text,
  decided timestamptz
);
create index if not exists account_recovery_account on public.account_recovery (account);
-- Wrong logins for a login name or email that no account has, as a one-way value: they lock like a real account's.
create table if not exists public.account_guard (
  k text primary key,
  fails integer not null default 0,
  locked_until timestamptz,
  at timestamptz not null default now()
);

-- Every function names its schema in full and runs with a fixed search_path (security audit 2026-11-12).
create or replace function public.account_fail(_status integer, _error text) returns jsonb
language sql immutable set search_path = '' as $$ select jsonb_build_object('status', _status, 'error', _error) $$;

create or replace function public.account_ms(_at timestamptz) returns bigint
language sql immutable set search_path = '' as $$ select (extract(epoch from _at) * 1000)::bigint $$;

create or replace function public.account_hour() returns bigint
language sql stable set search_path = '' as $$ select floor(extract(epoch from now()) / 3600)::bigint $$;

create or replace function public.account_over(_key text, _limit integer) returns boolean
language sql stable set search_path = '' as $$
  select coalesce((select n from public.account_hits where k = _key and hour = public.account_hour()), 0) >= _limit
$$;

create or replace function public.account_count(_key text) returns void
language sql set search_path = '' as $$
  insert into public.account_hits (k, hour, n) values (_key, public.account_hour(), 1)
  on conflict (k, hour) do update set n = public.account_hits.n + 1
$$;

-- A new session is a logged-in device (an older Worker sends no device: the name stays empty).
create or replace function public.account_session(_id text, _token text, b jsonb) returns void
language plpgsql set search_path = '' as $$
begin
  insert into public.account_sessions (token, account, expires, sid, created, last_seen, device_name, device_hash, country)
    values (_token, _id, now() + make_interval(days => (b->>'sessionDays')::integer), encode(extensions.gen_random_bytes(6), 'hex'),
            now(), now(), left(b->>'dname', 40), b->>'dhash', left(b->>'country', 2));
  delete from public.account_sessions where account = _id and token not in
    (select token from public.account_sessions where account = _id
     order by expires desc limit (b->>'sessionsKept')::integer);
end $$;

-- A new password ends every session; the recovery code changes with it.
create or replace function public.account_set_password(_id text, b jsonb) returns void
language plpgsql set search_path = public, extensions as $$
begin
  update public.accounts set hash = crypt(b->>'next', gen_salt('bf', 10)), recovery = b->>'nextProof',
    fails = 0, fail_at = null, locked_until = null, last_login = now() where id = _id;
  delete from public.account_sessions where account = _id;
  delete from public.account_tokens where account = _id and kind in ('reset', 'login2');
  perform public.account_session(_id, b->>'session', b);
end $$;

-- A line of the security log. The raw address comes only from a Worker that sends it ("addr").
create or replace function public.account_log(_id text, _event text, b jsonb) returns void
language sql set search_path = '' as $$
  insert into public.account_security_log (account, event, ip, country, device)
  values (_id, _event, left(coalesce(b->>'addr', ''), 64), left(coalesce(b->>'country', ''), 2), left(coalesce(b->>'dname', ''), 40))
$$;

-- A wrong password, recovery code or 2-step code: counted for the address and the account. Five lock the account.
-- Misses more than a day old are forgotten, as for a login name nobody has (account_guard_missed).
create or replace function public.account_missed(a public.accounts, b jsonb) returns jsonb
language plpgsql set search_path = '' as $$
declare
  n integer := case when coalesce(a.fail_at, now()) > now() - interval '1 day' then a.fails else 0 end;
  lock boolean := n + 1 >= (b->>'loginTries')::integer;
begin
  if b->>'ip' is not null then perform public.account_count('fail:' || (b->>'ip')); end if;
  update public.accounts set fails = case when lock then 0 else n + 1 end, fail_at = now(),
    locked_until = case when lock then now() + make_interval(mins => (b->>'lockMin')::integer) end
    where id = a.id;
  return public.account_fail(401, 'wrong');
end $$;

-- The same for a login name or email no account has (k: the Worker's one-way value of it; an older Worker sends none).
create or replace function public.account_guard_missed(_k text, b jsonb) returns jsonb
language plpgsql set search_path = '' as $$
declare
  g public.account_guard;
  n integer;
  lock boolean;
begin
  if b->>'ip' is not null then perform public.account_count('fail:' || (b->>'ip')); end if;
  if _k is null then return public.account_fail(401, 'wrong'); end if;
  select * into g from public.account_guard where k = _k for update;
  n := case when g.k is not null and g.at > now() - interval '1 day' then g.fails else 0 end;
  lock := n + 1 >= (b->>'loginTries')::integer;
  insert into public.account_guard (k, fails, locked_until, at)
    values (_k, case when lock then 0 else n + 1 end, case when lock then now() + make_interval(mins => (b->>'lockMin')::integer) end, now())
    on conflict (k) do update set fails = excluded.fails, locked_until = excluded.locked_until, at = excluded.at;
  return public.account_fail(401, 'wrong');
end $$;

create or replace function public.account_guarded(_k text) returns boolean
language sql stable set search_path = '' as $$
  select coalesce((select locked_until > now() from public.account_guard where k = _k), false)
$$;

-- A device that logs in: true when the account had logged in from other devices before and never from this one.
create or replace function public.account_device(_id text, b jsonb) returns boolean
language plpgsql set search_path = '' as $$
declare
  first boolean;
begin
  if b->>'dhash' is null then return false; end if;
  first := not exists (select 1 from public.account_devices where account = _id);
  update public.account_devices set last_seen = now(), name = coalesce(left(b->>'dname', 40), name) where account = _id and h = b->>'dhash';
  if found then return false; end if;
  insert into public.account_devices (account, h, name) values (_id, b->>'dhash', coalesce(left(b->>'dname', 40), ''));
  delete from public.account_devices where account = _id and h not in
    (select h from public.account_devices where account = _id order by last_seen desc limit (b->>'devicesKept')::integer);
  return not first;
end $$;

create or replace function public.account_member(_token text) returns setof public.accounts
language sql stable set search_path = '' as $$
  select a.* from public.account_sessions s join public.accounts a on a.id = s.account
  where s.token = _token and s.expires > now() and not a.blocked
$$;

-- The one entry point. Each call is one transaction.
create or replace function public.account_call(action text, b jsonb) returns jsonb
language plpgsql set search_path = public, extensions as $$
declare
  a public.accounts;
  inviter public.accounts;
  tok public.account_tokens;
  req public.account_recovery;
  invite text := coalesce(b->>'invite', '');
  add_days integer;
  ok boolean := false;
  q text := lower(trim(coalesce(b->>'q', '')));
  week timestamptz := now() - interval '7 days';
  members jsonb;
  summary jsonb;
begin
  -- housekeeping from the Worker's hourly job, which also keeps a free project from counting as idle
  if action = 'ping' then
    delete from account_hits where hour < account_hour();
    delete from account_sessions where expires < now();
    delete from account_tokens where expires < now();
    delete from account_security_log where at < now() - make_interval(days => coalesce((b->>'logDays')::integer, 30));
    delete from account_guard where at < now() - interval '1 day' and (locked_until is null or locked_until < now());
    delete from account_recovery where created < now() - make_interval(days => coalesce((b->>'recoveryKeepDays')::integer, 90));
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

  -- login by id or by email ("email" set), and the recovery code. A name nobody has is counted in account_guard and
  -- locks like an account, so neither the reply nor the lock tells which names exist.
  if action = 'login' or action = 'recover' then
    if account_over('fail:' || (b->>'ip'), (b->>'failsPerIp')::integer) then return account_fail(429, 'busy'); end if;
    if b->>'email' is not null then
      select * into a from accounts where email = b->>'email' for update;
    else
      select * into a from accounts where id = b->>'id' for update;
    end if;
    if a.id is null then
      if b->>'ident' is not null and account_guarded(b->>'ident') then
        if b->>'ip' is not null then perform account_count('fail:' || (b->>'ip')); end if;
        return account_fail(429, 'locked');
      end if;
      -- the same bcrypt work as a wrong password, so the time taken does not tell which ids exist
      if action = 'login' then perform crypt(b->>'pw', gen_salt('bf', 10)); end if;
      return account_guard_missed(b->>'ident', b);
    end if;
    if a.locked_until > now() then
      if b->>'ip' is not null then perform account_count('fail:' || (b->>'ip')); end if;
      return account_fail(429, 'locked');
    end if;
    if action = 'login' then
      if a.hash <> crypt(b->>'pw', a.hash) then
        perform account_log(a.id, 'login_failed', b);
        return account_missed(a, b);
      end if;
    elsif a.recovery <> b->>'proof' then
      return account_missed(a, b);
    end if;
    if a.blocked then return account_fail(403, 'blocked'); end if;
    -- "dry": the code is right; the Worker checks the new password against the email before anything changes
    if action = 'recover' and coalesce((b->>'dry')::boolean, false) then
      return jsonb_build_object('id', a.id, 'email', a.email, 'dry', true);
    end if;
    if action = 'recover' then
      perform account_set_password(a.id, b);
      perform account_log(a.id, 'password_changed', b);
      return jsonb_build_object('id', a.id, 'email', a.email);
    end if;
    -- 2-step login: no session yet, only a pending login (5 minutes, 5 tries); the misses stay until the code is right
    if a.totp is not null then
      if b->>'pending' is null then return account_fail(503, 'unavailable'); end if;
      insert into account_tokens (token, kind, account, data, expires)
        values (b->>'pending', 'login2', a.id, jsonb_build_object('dname', b->>'dname', 'dhash', b->>'dhash', 'country', b->>'country'),
                now() + make_interval(mins => (b->>'pendingMin')::integer));
      return jsonb_build_object('id', a.id, 'twofa', true);
    end if;
    update accounts set fails = 0, fail_at = null, last_login = now() where id = a.id;
    perform account_session(a.id, b->>'session', b);
    perform account_log(a.id, 'login', b);
    return jsonb_build_object('id', a.id, 'email', a.email, 'newDevice', account_device(a.id, b));
  end if;

  -- 2-step login, second step: "login2_get" spends one of the pending login's tries and hands the Worker the sealed
  -- secret; "login2" takes the Worker's verdict (a code step newer than the last one used, or a backup code).
  if action = 'login2_get' then
    if account_over('fail:' || (b->>'ip'), (b->>'failsPerIp')::integer) then return account_fail(429, 'busy'); end if;
    update account_tokens set tries = tries + 1
      where token = b->>'pending' and kind = 'login2' and expires > now() and tries < (b->>'pendingTries')::integer
      returning * into tok;
    if tok.token is null then return account_fail(400, 'expired'); end if;
    select * into a from accounts where id = tok.account;
    if a.id is null or a.totp is null then return account_fail(400, 'expired'); end if;
    if a.locked_until > now() then return account_fail(429, 'locked'); end if;
    return jsonb_build_object('id', a.id, 'totp', a.totp, 'step', a.totp_step);
  end if;
  if action = 'login2' then
    select * into tok from account_tokens where token = b->>'pending' and kind = 'login2' and expires > now() for update;
    if tok.token is null then return account_fail(400, 'expired'); end if;
    select * into a from accounts where id = tok.account for update;
    if a.id is null or a.totp is null then return account_fail(400, 'expired'); end if;
    if a.locked_until > now() then return account_fail(429, 'locked'); end if;
    if b->>'backup' is not null then
      delete from account_backup where account = a.id and h = b->>'backup';
      ok := found;
    elsif (b->>'step')::bigint > a.totp_step then
      update accounts set totp_step = (b->>'step')::bigint where id = a.id;
      ok := true;
    end if;
    if not ok then
      if tok.tries >= (b->>'pendingTries')::integer then delete from account_tokens where token = tok.token; end if;
      perform account_log(a.id, 'login_failed', b || tok.data);
      return account_missed(a, b);
    end if;
    if a.blocked then return account_fail(403, 'blocked'); end if;
    delete from account_tokens where token = tok.token;
    update accounts set fails = 0, fail_at = null, last_login = now() where id = a.id;
    perform account_session(a.id, b->>'session', b || tok.data);
    perform account_log(a.id, case when b->>'backup' is not null then 'backup_used' else 'login' end, b || tok.data);
    return jsonb_build_object('id', a.id, 'email', a.email, 'newDevice', account_device(a.id, b || tok.data), 'device', tok.data->>'dname',
                              'backupLeft', (select count(*) from account_backup where account = a.id));
  end if;

  -- email sign-up: a waiting sign-up and a "verify" mail; an address that has an account gets an "already" mail. Both
  -- run bcrypt and write a row, so the reply and its time are the same.
  if action = 'signup_email' then
    if account_over('signup:' || (b->>'ip'), (b->>'signupsPerIp')::integer)
       or account_over('signup:all', (b->>'signupsPerHour')::integer) then
      return account_fail(429, 'busy');
    end if;
    if invite <> '' and not exists (select 1 from accounts where invite_code = invite and not blocked) then
      return account_fail(404, 'unknown');
    end if;
    perform account_count('signup:' || (b->>'ip'));
    perform account_count('signup:all');
    if exists (select 1 from accounts where email = b->>'email') then
      perform crypt(b->>'pw', gen_salt('bf', 10));
      insert into account_tokens (token, kind, expires) values (b->>'t', 'noop', now() + interval '1 hour');
      return jsonb_build_object('mail', 'already');
    end if;
    insert into account_tokens (token, kind, email, hash, data, expires)
      values (b->>'t', 'signup', b->>'email', crypt(b->>'pw', gen_salt('bf', 10)),
              jsonb_build_object('consent', b->>'consent', 'invite', invite), now() + make_interval(hours => (b->>'signupHours')::integer));
    return jsonb_build_object('mail', 'verify');
  end if;

  -- the link in a "verify" mail: a new member (email sign-up) or an email added to an id account
  if action = 'verify' then
    select * into tok from account_tokens where token = b->>'t' and kind in ('signup', 'email') and expires > now() for update;
    if tok.token is null then return account_fail(400, 'expired'); end if;
    if exists (select 1 from accounts where email = tok.email) then
      delete from account_tokens where token = tok.token;
      return account_fail(409, 'already');
    end if;
    if tok.kind = 'email' then
      update accounts set email = tok.email, email_at = now() where id = tok.account and email is null;
      if not found then
        delete from account_tokens where token = tok.token;
        return account_fail(400, 'expired');
      end if;
      delete from account_tokens where kind in ('signup', 'email') and email = tok.email;
      perform account_log(tok.account, 'email_added', b);
      return jsonb_build_object('id', tok.account, 'email', tok.email, 'mail', 'email_added');
    end if;
    invite := coalesce(tok.data->>'invite', '');
    if invite <> '' then
      select * into inviter from accounts where invite_code = invite and not blocked and invites < (b->>'inviteMax')::integer for update;
    end if;
    insert into accounts (id, hash, recovery, consent, invited_by, ticket_percent, ticket_expires, email, email_at)
      values (b->>'id', tok.hash, b->>'proof', coalesce(tok.data->>'consent', ''), inviter.id,
              case when inviter.id is not null then (b->>'ticketPercent')::integer end,
              case when inviter.id is not null then now() + make_interval(days => (b->>'ticketDays')::integer) end,
              tok.email, now())
      on conflict (id) do nothing;
    if not found then return account_fail(409, 'retry'); end if;
    if inviter.id is not null then
      add_days := least((b->>'rewardDays')::integer, greatest(0, (b->>'rewardCap')::integer - inviter.earned_days));
      update accounts set invites = invites + 1, reward_days = reward_days + add_days,
        earned_days = earned_days + add_days where id = inviter.id;
    end if;
    delete from account_tokens where kind in ('signup', 'email') and email = tok.email;
    return jsonb_build_object('id', b->>'id', 'email', tok.email);
  end if;

  -- "forgot password": a reset link (only the newest works) when a verified email has an account; a "noop" row otherwise
  if action = 'forgot' then
    if account_over('forgot:' || (b->>'ip'), (b->>'mailPerIp')::integer) then return account_fail(429, 'busy'); end if;
    perform account_count('forgot:' || (b->>'ip'));
    select * into a from accounts where email = b->>'email' and not blocked;
    if a.id is null then
      insert into account_tokens (token, kind, expires) values (b->>'t', 'noop', now() + interval '1 hour');
      return jsonb_build_object('sent', true);
    end if;
    delete from account_tokens where account = a.id and kind = 'reset';
    insert into account_tokens (token, kind, account, email, expires)
      values (b->>'t', 'reset', a.id, a.email, now() + make_interval(mins => (b->>'resetMin')::integer));
    return jsonb_build_object('sent', true, 'mail', 'reset', 'email', a.email);
  end if;

  -- the reset link: "reset_peek" names the account for the password rule, "reset" spends the link
  if action = 'reset_peek' or action = 'reset' then
    select * into tok from account_tokens where token = b->>'t' and kind = 'reset' and expires > now() for update;
    if tok.token is null then return account_fail(400, 'expired'); end if;
    select * into a from accounts where id = tok.account for update;
    if a.id is null then return account_fail(400, 'expired'); end if;
    if action = 'reset_peek' then return jsonb_build_object('id', a.id, 'email', a.email); end if;
    update accounts set hash = crypt(b->>'next', gen_salt('bf', 10)), fails = 0, fail_at = null, locked_until = null where id = a.id;
    delete from account_sessions where account = a.id;
    delete from account_tokens where account = a.id and kind in ('reset', 'login2');
    perform account_log(a.id, 'password_changed', b);
    return jsonb_build_object('id', a.id, 'email', a.email);
  end if;

  -- the link in a "link_key" mail: the key joins the account that asked
  if action = 'link_confirm' then
    select * into tok from account_tokens where token = b->>'t' and kind = 'link' and expires > now() for update;
    if tok.token is null or tok.account is null then return account_fail(400, 'expired'); end if;
    insert into account_keys (account, key_id) values (tok.account, tok.data->>'key') on conflict do nothing;
    delete from account_tokens where token = tok.token;
    perform account_log(tok.account, 'key_linked', b);
    return jsonb_build_object('id', tok.account, 'key', tok.data->>'key');
  end if;

  -- a recovery request (2-step device and backup codes lost): one open request per account, decided by the seller after
  -- "due". The reply is the same whether the name exists or not.
  if action = 'recovery_request' then
    if account_over('recovery:' || (b->>'ip'), (b->>'mailPerIp')::integer) then return account_fail(429, 'busy'); end if;
    perform account_count('recovery:' || (b->>'ip'));
    if b->>'email' is not null then
      select * into a from accounts where email = b->>'email';
    else
      select * into a from accounts where id = b->>'id';
    end if;
    if a.id is null or exists (select 1 from account_recovery where account = a.id and status = 'open') then
      insert into account_tokens (token, kind, expires) values (b->>'t', 'noop', now() + interval '1 hour');
      return jsonb_build_object('sent', true);
    end if;
    insert into account_recovery (id, account, due, facts)
      values (b->>'rid', a.id, now() + make_interval(hours => (b->>'recoveryHours')::integer), coalesce(b->'facts', '{}'::jsonb));
    insert into account_tokens (token, kind, account, data, expires)
      values (b->>'t', 'cancel', a.id, jsonb_build_object('request', b->>'rid'), now() + make_interval(days => (b->>'recoveryKeepDays')::integer));
    perform account_log(a.id, 'recovery_requested', b);
    return jsonb_build_object('sent', true, 'mail', case when a.email is not null then 'recovery_requested' end, 'email', a.email,
                              'due', account_ms(now() + make_interval(hours => (b->>'recoveryHours')::integer)));
  end if;
  if action = 'recovery_cancel' then
    select * into tok from account_tokens where token = b->>'t' and kind = 'cancel' and expires > now() for update;
    if tok.token is null then return account_fail(400, 'expired'); end if;
    update account_recovery set status = 'cancelled', decided = now() where id = tok.data->>'request' and account = tok.account and status = 'open';
    if not found then return account_fail(409, 'closed'); end if;
    delete from account_tokens where token = tok.token;
    perform account_log(tok.account, 'recovery_cancelled', b);
    return jsonb_build_object('ok', true);
  end if;

  if action = 'admin/list' then
    select coalesce(jsonb_agg(m), '[]'::jsonb) into members from (
      select id, email, account_ms(created) as created, account_ms(last_login) as "lastLogin", invite_code as code,
             invited_by as "invitedBy", invites, earned_days as earned, reward_days as waiting, blocked::integer as blocked,
             (totp is not null)::integer as twofa
      from accounts
      where q = '' or position(q in id) > 0 or position(q in lower(coalesce(invite_code, ''))) > 0 or position(q in coalesce(email, '')) > 0
      order by accounts.created desc limit 200) m;
    select jsonb_build_object('total', count(*), 'week', count(*) filter (where created > week),
             'active', count(*) filter (where last_login > week), 'blocked', count(*) filter (where blocked),
             'invited', count(*) filter (where invited_by is not null), 'email', count(*) filter (where email is not null),
             'twofa', count(*) filter (where totp is not null)) into summary from accounts;
    return jsonb_build_object('members', members, 'summary', summary);
  end if;

  if action = 'admin/block' or action = 'admin/recovery' or action = 'admin/delete' then
    if not exists (select 1 from accounts where id = b->>'id') then return account_fail(404, '없는 회원이에요'); end if;
    if action = 'admin/block' then
      update accounts set blocked = (b->>'on')::boolean where id = b->>'id';
      if (b->>'on')::boolean then delete from account_sessions where account = b->>'id'; end if;
    elsif action = 'admin/recovery' then
      update accounts set recovery = b->>'proof', fails = 0, fail_at = null, locked_until = null where id = b->>'id';
    else
      delete from account_tokens where email = (select email from accounts where id = b->>'id');
      delete from accounts where id = b->>'id';
      if b->>'gone' is not null then insert into account_gone (h) values (b->>'gone') on conflict (h) do nothing; end if;
    end if;
    return jsonb_build_object('ok', true);
  end if;

  -- the dashboard's 복구 요청: list, approve (only after "due": 2-step login off, every session ended, and a reset link
  -- for an account with an email or a new recovery code for one without), reject
  if action = 'admin/recovery_list' then
    select coalesce(jsonb_agg(r order by r.created desc), '[]'::jsonb) into members from (
      select x.id, x.account, a2.email, account_ms(x.created) as created, account_ms(x.due) as due, x.status, x.facts, x.reason,
             account_ms(x.decided) as decided, (a2.totp is not null) as twofa, a2.blocked
      from account_recovery x join accounts a2 on a2.id = x.account
      where q <> 'open' or x.status = 'open'
      order by x.created desc limit 200) r;
    return jsonb_build_object('requests', members);
  end if;
  if action = 'admin/recovery_approve' or action = 'admin/recovery_reject' then
    select * into req from account_recovery where id = b->>'id' for update;
    if req.id is null then return account_fail(404, '없는 요청이에요'); end if;
    if req.status <> 'open' then return account_fail(409, '이미 처리된 요청이에요'); end if;
    if action = 'admin/recovery_reject' then
      update account_recovery set status = 'rejected', reason = left(b->>'reason', 200), decided = now() where id = req.id;
      delete from account_tokens where kind = 'cancel' and data->>'request' = req.id;
      perform account_log(req.account, 'recovery_rejected', '{}'::jsonb);
      return jsonb_build_object('ok', true, 'account', req.account);
    end if;
    if req.due > now() then return account_fail(409, '72시간 대기가 끝나지 않았어요'); end if;
    select * into a from accounts where id = req.account for update;
    if a.email is not null and not coalesce((b->>'mail')::boolean, false) then
      return account_fail(409, '메일 발송이 꺼져 있어 메일 계정의 복구를 승인할 수 없어요');
    end if;
    update accounts set totp = null, totp_pending = null, totp_step = 0, fails = 0, fail_at = null, locked_until = null where id = a.id;
    delete from account_backup where account = a.id;
    delete from account_sessions where account = a.id;
    delete from account_tokens where account = a.id and kind in ('reset', 'login2');
    if a.email is not null then
      insert into account_tokens (token, kind, account, email, expires)
        values (b->>'t', 'reset', a.id, a.email, now() + make_interval(hours => (b->>'approvedHours')::integer));
    else
      update accounts set recovery = b->>'proof' where id = a.id;
    end if;
    update account_recovery set status = 'approved', decided = now() where id = req.id;
    delete from account_tokens where kind = 'cancel' and data->>'request' = req.id;
    perform account_log(a.id, 'recovery_approved', '{}'::jsonb);
    return jsonb_build_object('ok', true, 'account', a.id, 'email', a.email, 'lang', req.facts->>'lang');
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
  update account_sessions set last_seen = now() where token = b->>'token' and last_seen < now() - interval '10 minutes';

  if action = 'me' then
    return jsonb_build_object('id', a.id, 'created', account_ms(a.created), 'code', a.invite_code, 'invites', a.invites,
                              'rewardDays', a.reward_days, 'earnedDays', a.earned_days,
                              'ticket', case when a.ticket_percent is not null and a.ticket_used is null and a.ticket_expires > now()
                                             then jsonb_build_object('percent', a.ticket_percent, 'expires', account_ms(a.ticket_expires)) end,
                              'email', a.email, 'emailVerified', a.email is not null, 'twofa', a.totp is not null,
                              'backupLeft', (select count(*) from account_backup where account = a.id));
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

  -- logged-in devices (one per session), and ending one or all of them; a device id of another account ends nothing
  if action = 'devices' then
    return jsonb_build_object('devices', coalesce((select jsonb_agg(jsonb_build_object('id', s.sid, 'name', coalesce(nullif(s.device_name, ''), 'SystemBoost app'),
             'country', coalesce(s.country, ''), 'created', account_ms(s.created), 'lastSeen', account_ms(s.last_seen),
             'current', s.token = b->>'token') order by s.last_seen desc)
           from account_sessions s where s.account = a.id and s.expires > now()), '[]'::jsonb));
  end if;
  if action = 'logout_device' then
    delete from account_sessions where account = a.id and sid = b->>'sid';
    if not found then return account_fail(404, 'device'); end if;
    return jsonb_build_object('ok', true);
  end if;
  if action = 'logout_all' then
    delete from account_sessions where account = a.id and (not coalesce((b->>'keep')::boolean, false) or token <> b->>'token');
    perform account_log(a.id, 'logout_all', b);
    return jsonb_build_object('ok', true);
  end if;

  -- the 2-step login's sealed secrets, for the Worker to check a code with
  if action = 'totp_get' then
    return jsonb_build_object('id', a.id, 'email', a.email, 'totp', a.totp, 'pending', a.totp_pending, 'step', a.totp_step);
  end if;
  -- the code from the authenticator app matched the pending secret (totp_start): 2-step login on, new backup codes
  if action = 'totp_on' then
    if a.totp is not null then return account_fail(409, 'already'); end if;
    if a.totp_pending is null or a.totp_pending <> b->>'pending' then return account_fail(409, 'nothing'); end if;
    update accounts set totp = totp_pending, totp_pending = null, totp_step = (b->>'step')::bigint where id = a.id;
    delete from account_backup where account = a.id;
    insert into account_backup (account, h) select a.id, x from jsonb_array_elements_text(b->'backup') x;
    perform account_log(a.id, '2fa_on', b);
    return jsonb_build_object('ok', true);
  end if;

  if action = 'keys' then
    return jsonb_build_object('email', a.email, 'keys', coalesce((select jsonb_agg(key_id order by linked) from account_keys where account = a.id), '[]'::jsonb));
  end if;
  -- a key to link: the link goes to the key's purchase email; only the newest link for a key works
  if action = 'link_key' then
    if account_over('mail:' || (b->>'ip'), (b->>'mailPerIp')::integer) then return account_fail(429, 'busy'); end if;
    perform account_count('mail:' || (b->>'ip'));
    if (select count(*) from account_keys where account = a.id) >= (b->>'keysKept')::integer then return account_fail(409, 'full'); end if;
    delete from account_tokens where account = a.id and kind = 'link' and data->>'key' = b->>'key';
    insert into account_tokens (token, kind, account, data, expires)
      values (b->>'t', 'link', a.id, jsonb_build_object('key', b->>'key'), now() + make_interval(hours => (b->>'linkHours')::integer));
    return jsonb_build_object('ok', true);
  end if;

  if action = 'consent' then
    insert into account_consents (account, kind, version, gpu, app, withdrawn)
      values (a.id, b->>'kind', b->>'version', coalesce(b->>'gpu', ''), coalesce(b->>'app', ''), coalesce((b->>'withdraw')::boolean, false));
    delete from account_consents where account = a.id and ctid not in
      (select ctid from account_consents where account = a.id order by at desc limit (b->>'consentsKept')::integer);
    return jsonb_build_object('ok', true);
  end if;

  -- the password again: the same limits and lock as a login, so a session cannot guess it (or run bcrypt) without end
  if action in ('password', 'delete', 'totp_start', 'totp_off', 'backup_new', 'add_email') then
    if account_over('fail:' || (b->>'ip'), (b->>'failsPerIp')::integer) then return account_fail(429, 'busy'); end if;
    if action = 'add_email' and account_over('mail:' || (b->>'ip'), (b->>'mailPerIp')::integer) then return account_fail(429, 'busy'); end if;
    select * into a from accounts where id = a.id for update;
    if a.locked_until > now() then return account_fail(429, 'locked'); end if;
    if a.hash <> crypt(b->>'pw', a.hash) then return account_missed(a, b); end if;
    if action = 'password' then
      perform account_set_password(a.id, b);
      perform account_log(a.id, 'password_changed', b);
      return jsonb_build_object('id', a.id, 'email', a.email);
    end if;
    if action = 'totp_start' then
      if a.totp is not null then return account_fail(409, 'already'); end if;
      update accounts set totp_pending = b->>'pending' where id = a.id;
      return jsonb_build_object('ok', true);
    end if;
    if action = 'totp_off' then
      if a.totp is null then return account_fail(409, 'off'); end if;
      if b->>'backup' is not null then
        delete from account_backup where account = a.id and h = b->>'backup';
        ok := found;
      elsif (b->>'step')::bigint > a.totp_step then
        ok := true;
      end if;
      if not ok then return account_missed(a, b); end if;
      update accounts set totp = null, totp_pending = null, totp_step = 0, fails = 0, fail_at = null where id = a.id;
      delete from account_backup where account = a.id;
      perform account_log(a.id, '2fa_off', b);
      return jsonb_build_object('ok', true);
    end if;
    if action = 'backup_new' then
      if a.totp is null then return account_fail(409, 'off'); end if;
      delete from account_backup where account = a.id;
      insert into account_backup (account, h) select a.id, x from jsonb_array_elements_text(b->'backup') x;
      perform account_log(a.id, 'backup_new', b);
      return jsonb_build_object('ok', true);
    end if;
    if action = 'add_email' then
      perform account_count('mail:' || (b->>'ip'));
      if a.email is not null then return account_fail(409, 'has_email'); end if;
      if exists (select 1 from accounts where email = b->>'email') then return jsonb_build_object('mail', 'already'); end if;
      delete from account_tokens where account = a.id and kind = 'email';
      insert into account_tokens (token, kind, account, email, expires)
        values (b->>'t', 'email', a.id, b->>'email', now() + make_interval(hours => (b->>'signupHours')::integer));
      return jsonb_build_object('mail', 'verify');
    end if;
    -- leaving: pending links for the address go too; everything else of the member goes with the account (cascade)
    delete from account_tokens where a.email is not null and email = a.email;
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
alter table public.account_tokens enable row level security;
alter table public.account_backup enable row level security;
alter table public.account_devices enable row level security;
alter table public.account_keys enable row level security;
alter table public.account_consents enable row level security;
alter table public.account_security_log enable row level security;
alter table public.account_recovery enable row level security;
alter table public.account_guard enable row level security;
revoke all on public.accounts, public.account_sessions, public.account_hits, public.account_gone, public.account_tokens, public.account_backup,
  public.account_devices, public.account_keys, public.account_consents, public.account_security_log, public.account_recovery, public.account_guard
  from public, anon, authenticated;
grant select, insert, update, delete on public.accounts, public.account_sessions, public.account_hits, public.account_gone, public.account_tokens,
  public.account_backup, public.account_devices, public.account_keys, public.account_consents, public.account_security_log, public.account_recovery,
  public.account_guard to service_role;
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
  foreach t in array array['public.accounts', 'public.account_sessions', 'public.account_hits', 'public.account_gone', 'public.account_tokens',
                           'public.account_backup', 'public.account_devices', 'public.account_keys', 'public.account_consents',
                           'public.account_security_log', 'public.account_recovery', 'public.account_guard']::regclass[] loop
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
select 'SystemBoost member database: accounts v2 applied' as result;
