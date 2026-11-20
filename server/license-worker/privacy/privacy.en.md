# SystemBoost privacy policy (summary)

This page is a summary in English. The Korean text is the policy; if the two differ, the Korean text governs.

Effective date: {{EFFECTIVE_DATE}}

## At a glance

- The app works without an account. You sign up with an ID or an email address; the email address is taken only if you sign up with it or add it, and no name is asked for.
- Card numbers are entered only in the Lemon Squeezy checkout. They never reach the SystemBoost app or the seller's server, and the server stores nothing about cards.
- The seller's server runs on Cloudflare (a US company), member accounts are kept in Supabase and mail is sent through Resend (a US company), so personal information is processed and stored outside Korea.
- Some records have no set retention period (key records and the email index among them). The Korean policy, Article 2, lists every record and how long it stays.
- Personal information is not used for advertising or marketing (no advertising mail), and is not sold or given to anyone.
- A member's security log keeps the IP address as it is for 30 days, to look into account takeovers. Everywhere else the server keeps IP addresses only as hashes.
- Usage statistics are sent only if you turn them on. From version 0.8.14 they are off at first.

## What is processed

- Starting an app with a registered key, and at most once every 20 hours while it runs: the key ID (16 characters computed from the key), to check whether the key was blocked. Not stored.
- Renewing a subscription key: the subscription number and the key in use (only checked against that subscription). Not stored.
- Bank transfer and direct payment: the order code, plan, status and times, the payer name and payment memo you type, a discount ticket's percentage; when approved, the key ID and an email address the seller may enter; a rejection reason or refund time.
- Card payment: from Lemon Squeezy, the payment email, subscription number, order number, payment (invoice) number, plan, next payment date and whether it is a test payment. Other details in Lemon Squeezy's notice (such as the name) are not stored.
- Every key: the full key text and its record (plan, dates, how it was received (direct payment, card, referral or invite, issued by the seller), email address, note, subscription number, blocked or not). The note can hold the order code, the payer name and memo of a direct payment, a referral code, or a member ID. The key itself contains the email address, so showing the key to someone shows the email address too.
- Referral and invite codes: the code and a random device number made by the app.
- A price change notice of a card subscription: when you answer, the subscription number, your answer (agree or cancel), the time, the key ID, whether Lemon Squeezy took the cancel, and when the seller moved the subscription to the new price; the key you send is only checked, not stored. Purpose: a record that you agreed before the new price is charged, and cancelling the subscription if you choose to.
- Members (optional): ID (a member number made by the server when you sign up by email), the email address (if you signed up with it or added it) and when it was confirmed, password and recovery code as hashes only, sign-up and last login times, the consent text version and time, invite codes and relations, rewards, discount tickets (including the order code they were used on), failed logins and lockout, blocked or not, login sessions (as hashes, with a session number, creation and last access times, device name, a hash of the device number and the country from Cloudflare), signed-in devices (hash of the device number, name, first and last access), and the key IDs linked to the account.
- 2-step login (optional): the authenticator secret (encrypted), the time step of the last code used, and backup codes as hashes only.
- Member security log: the time and kind of each login, wrong password, password change or reset, 2-step login on or off, backup code use or renewal, email added, key linked, sign-out everywhere, and account recovery request, cancel, approval or rejection, with the IP address as it is, the country and the device name. Purpose: looking into account takeovers and deciding recovery requests.
- Mail confirmations and links (members): a hash of the link, its kind (sign-up, adding an email, password reset, 2-step login, key linking, cancelling a recovery request), the member ID or email address, wrong tries, creation and expiry times; while signing up by email also the password hash, consent version and invite code; during a 2-step login also the device name and a hash of the device number.
- Overclocking responsibility notice (a signed-in member in the app): the notice version, GPU name, app version, consent or withdrawal, and the time. Purpose: a record that the notice was accepted before overclocking.
- Account recovery requests (a member who lost both the password and the recovery code): the member ID found from the ID or email given and, only if you give them, contact, order code, payment date, amount, last 4 digits of the card, payer name and a message; the language, request time, the time it can be decided (72 hours later), status, rejection reason and decision time. The seller checks them against payment records.
- Getting a key again by mail (/keys/resend, also for non-members): the email address you type, used only to find the key bought with it and send it there; not stored.
- Mail: the recipient address and the content (links, member ID, device name, country, time; the full key, plan and expiry when a key is sent again; the key ID when linking a key). Mail is used only for these notices and confirmations and for the one pre-registration launch email, without open or click tracking.
- Support requests and refund requests: category, subject, messages and replies, times, status, consent time, language, the access code as a hash, the member ID when sent from a signed-in app, and what you choose to give (contact, order code, key ID, app details). A refund request also carries the order and key details the server holds. A full key sent for a check is never stored.
- Usage statistics (optional: sent only if 'Send usage statistics' is turned on, on the agreement screen or in Settings; off at first from version 0.8.14, ticked at first in earlier versions; nothing is sent before the agreement is accepted): a random install number, app version, operating system, plan, frame generation multiplier, one-click boost use, the country (from Cloudflare), first and last connection. No IP address is stored with them.
- Pre-registration on the official site (until November 27, 2026, only with consent; 14 or older): the email address, plan of interest, site language and sign-up time. Purpose: one launch email; no other mail.
- Abuse limits: hashes made from IP addresses with a value only the server knows (sign-up, login, member mail requests such as password reset, recovery and key resend, support, orders and payment requests, referral codes, wrong dashboard passwords), failed logins and lockout for IDs or emails without an account (as hashes), mails sent per email address (as hashes), and a daily count per key ID of subscription-management links handed out.
- The seller's activity log: what the seller did and automatic steps, with details such as order codes, key IDs, subscription numbers, member IDs, email addresses (masked) and key notes, payer names and memos (first character only).
- The dashboard access log: each dashboard sign-in, each view of member or purchase data and each download of the pre-registration email list, with the time, a one-way hash of the address and the screen and item viewed (never search words).
- Deleted member IDs: a salted hash of an ID that was deleted, so nobody else can register the same ID.
- If the seller turns on Cloudflare's logs: request addresses and server error records, for the period Cloudflare sets.
- Background checks by the running app: while an order waits (up to 90 days after ordering), its order code every minute for the first hour, every 15 minutes for the first day, then hourly (a card checkout every 10 seconds for its first 10 minutes), until the key arrives or the order is rejected, refunded or deleted; an order the app stopped waiting for (cancelled in the app, or older than 90 days) is asked about again when the '★ Plans' page opens, for up to 40 days and at most hourly; a subscription key, from 7 days before its end until 45 days after it, every 6 hours (and after the PC wakes or the network returns), with its subscription number and the key in use, to fetch the next period's key (once a day after the server answered that the subscription ended); while a support request waits for the seller's answer (changed in the last 14 days), its number and access code every 30 minutes (up to every 2 hours when the server is busy). None of these checks leaves a new record on the server.

## How long

- Orders: 90 days from the last change. Key-collection records: 7 days (card) or 30 days (direct payment).
- Key records, the email index, links between Lemon Squeezy orders and keys, blocked keys, subscription plans and referral codes: no set retention period, kept until the seller deletes them.
- Members: until they leave or the seller deletes the account; leaving deletes the account from the live database at once (with its email address, sessions, devices, 2-step login, backup codes, linked keys, consent records, security log, recovery requests and pending links), and Supabase backups keep it up to 7 more days. Keys and payment records (orders, key records) are not deleted, and keys keep working without the account. The member ID stays in invite-reward key notes (no set period), the activity log (180 days) and support requests (their own period).
- Login sessions: 90 days (the newest 5 per member). Signed-in devices: the 20 most recently used per member. Security log: 30 days. Mail links: sign-up and adding an email 24 hours, password reset 30 minutes (24 hours when sent after an approved recovery), 2-step login 5 minutes, key linking 24 hours, cancelling a recovery request 90 days (or until it is decided). Overclocking consent records: the newest 50 per member, until the account is deleted. Recovery requests: 90 days. Failed logins for IDs or emails without an account: 1 day after the last miss. Mail and key-resend counts: 1 hour. 2-step secrets and backup codes: until 2-step login is turned off (an approved recovery turns it off).
- Sign-up, login and member mail request IP hashes: a little over an hour. Support, order and referral IP hashes: up to 1 hour after the last request.
- Pre-registration: all deleted on February 28, 2027, or at once on request; email lists the seller downloads to send the launch email, and the sent copies in the seller's Naver Mail, are deleted that day too.
- Referral and invite device numbers: 90 days. Answers to a price change notice: 3 years from the answer.
- Support requests: 3 years for payment and refund requests, 1 year for the rest, from the last change.
- Usage statistics: 90 days after the last connection. Activity log: 180 days. Dashboard access log: one year. Wrong dashboard password counts (by address hash): 15 minutes after the last miss. Daily portal-link counts: 2 days.
- Hashes of deleted member IDs: no set retention period, kept until the seller deletes them.
- A subscription's list of key IDs: 400 days after a key was last added. Marks that a card payment notice was already handled (subscription and payment numbers): 400 days.
- Backups: deleted records stay for a while in the providers' recovery copies, up to 30 days for Cloudflare's database (usage statistics, daily totals, the dashboard's news, pre-registration) and up to 7 days for member accounts in Supabase. They are used only to recover from a failure, and anything deleted before a restore is deleted again.
- Requests saved by /help in your browser: until you remove them.

## Sharing, processors and transfers abroad

- Personal information is not given to third parties except where the law requires it. Discord announcements carry only what the seller writes.
- If the seller turns it on, a channel only the seller reads gets an alert through Discord Inc. (USA) when a buyer says a bank transfer was paid, and once more if it is still waiting 20 hours later: the order code, plan and the deadline for the key only, never an email address, name, payer name, memo or IP address. Kept until the seller deletes the message. If the seller turns it on, that channel also gets the number of new pre-registrations on the official site (by plan and language), never an email address.
- Cloudflare, Inc. (USA, dpo@cloudflare.com) runs the server and stores its data in data centers worldwide.
- Supabase Pte. Ltd. (privacy@supabase.com) stores member accounts, including the security log with IP addresses. Database location: {{SUPABASE_REGION}}
- Resend, Inc. (USA) sends member mail and keys sent again by mail: the recipient and sender address, subject and content. Kept for the period Resend sets.
- NAVER Corporation (Republic of Korea; not a transfer abroad) carries the one launch email for pre-registration, sent from the seller's Naver Mail account as blind copies so recipients do not see each other's addresses: the recipient and sender address, subject and content. Kept until the seller deletes the sent copies on February 28, 2027.
- Lemon Squeezy handles card payments under its own privacy policy and receives the order code and, for the subscription page, the subscription number.
- The app itself connects to GitHub for updates (this can be turned off in Settings).

## Your rights

- You can ask to see, correct or delete your information, to stop its processing, or withdraw consent: through Support in the app, /help, or the email address below. The seller answers within 10 days.
- Under '◈ Account' you can add an email address, see signed-in devices and sign them out, turn 2-step login on or off and make new backup codes. The overclocking consent is withdrawn with 'Withdraw consent' under 'GPU · Overclock'; in an app signed in as a member, the withdrawal is recorded with the account too. A forgotten password can be reset by mail (with a confirmed email) or with the recovery code; without either you can ask for account recovery, which is announced by mail when the account has one, can be cancelled for 72 hours, and is then checked by the seller.
- You can delete your account under '◈ Account' → 'Delete account', turn off 'Send usage statistics' on the agreement screen or in Settings, close your requests, and remove saved requests on /help ('Remove from list').
- Sign-up is for people aged 14 or over. So is pre-registration on the official site: the box "I am 14 or older and agree" must be ticked (age is not asked or stored).

## EU, EEA and UK users

This applies where the GDPR or the UK GDPR covers you, together with the rest of this policy.

- Controller: the seller; contact details below.
- Legal bases
  - Contract: issuing, checking and renewing keys, orders and payment checks, consent to or cancelling over a card subscription price change, member features, answering requests, withdrawal and refunds.
  - Consent: usage statistics, sent only if you turn them on, and pre-registration on the official site (one launch email). You can withdraw consent at any time: turn usage statistics off in Settings, or ask as under 'Your rights' to delete a pre-registration; this does not make earlier processing unlawful.
  - Legitimate interests: preventing abuse: IP address hashes to stop too many requests, stopping dashboard password guessing, logging the seller's actions and dashboard access, the member security log (with IP addresses) to look into account takeovers, and the record of the overclocking responsibility consent. You can object to this processing.
- Your rights: access, rectification, erasure, restriction of processing, data portability, objection (to processing based on legitimate interests) and withdrawal of consent. Ask as described under 'Your rights'.
- Complaints: you can complain to the data protection supervisory authority where you live, work or think the infringement happened; in the UK, the Information Commissioner's Office (ICO).
- International transfers: personal information goes to the processors named above: Cloudflare (a US company, data centers worldwide), Supabase (database location above), Resend (USA) when mail is sent, Lemon Squeezy if you pay by card, and Discord (USA) if the seller turned on alerts; the app connects to GitHub (USA) for updates. Safeguards for these transfers follow the providers' terms.

## Contact

- Privacy officer: {{OWNER_NAME}}
- Email: {{OWNER_EMAIL}}
- Korean authorities for privacy complaints: Personal Information Dispute Mediation Committee 1833-6972, KISA Privacy Infringement Report Center 118, Korean National Police Agency Cyber Bureau 182.
