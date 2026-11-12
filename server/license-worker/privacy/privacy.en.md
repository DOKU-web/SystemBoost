# SystemBoost privacy policy (summary)

This page is a summary in English. The Korean text is the policy; if the two differ, the Korean text governs.

Effective date: {{EFFECTIVE_DATE}}

## At a glance

- The app works without an account. Sign-up asks for no email address or name.
- Card numbers are entered only in the Lemon Squeezy checkout. They never reach the SystemBoost app or the seller's server, and the server stores nothing about cards.
- The seller's server runs on Cloudflare (a US company) and member accounts are kept in Supabase, so personal information is processed and stored outside Korea.
- Some records have no set retention period (key records and the email index among them). The Korean policy, Article 2, lists every record and how long it stays.
- Personal information is not used for advertising or marketing, and is not sold or given to anyone.

## What is processed

- Starting an app with a registered key: the key ID (16 characters computed from the key), to check whether the key was blocked. Not stored.
- Renewing a subscription key: the subscription number and the key in use (only checked against that subscription). Not stored.
- Bank transfer and direct payment: the order code, plan, status and times, the payer name and payment memo you type, a discount ticket's percentage; when approved, the key ID and an email address the seller may enter; a rejection reason or refund time.
- Card payment: from Lemon Squeezy, the payment email, subscription number, order number, plan and next payment date. Other details in Lemon Squeezy's notice (such as the name) are not stored.
- Every key: the full key text and its record (plan, dates, how it was received, email address, note, subscription number, blocked or not). The note can hold the order code, the payer name and memo of a direct payment, a referral code, or a member ID. The key itself contains the email address, so showing the key to someone shows the email address too.
- Referral and invite codes: the code and a random device number made by the app.
- Members (optional): ID, password and recovery code as hashes only, sign-up and last login times, the consent text version and time, invite codes and relations, rewards, discount tickets (including the order code they were used on), failed logins and lockout, blocked or not, login sessions as hashes.
- Support requests and refund requests: category, subject, messages and replies, times, status, consent time, language, the access code as a hash, the member ID when sent from a signed-in app, and what you choose to give (contact, order code, key ID, app details). A refund request also carries the order and key details the server holds. A full key sent for a check is never stored.
- Usage statistics (on by default, can be turned off in Settings): a random install number, app version, operating system, plan, frame generation multiplier, one-click boost use, the country (from Cloudflare), first and last connection. No IP address is stored with them.
- Abuse limits: hashes made from IP addresses with a value only the server knows (sign-up, login, support), and the plain IP address of a wrong dashboard password, for 15 minutes after the last miss.
- The seller's activity log: what the seller did and automatic steps, with details such as order codes, key IDs, email addresses, key notes and member IDs.
- If the seller turns on Cloudflare's logs: request addresses and server error records, for the period Cloudflare sets.

## How long

- Orders: 90 days from the last change. Key-collection records: 7 days (card) or 30 days (direct payment).
- Key records, the email index, links between Lemon Squeezy orders and keys, blocked keys, subscription plans and referral codes: no set retention period, kept until the seller deletes them.
- Members: until they leave or the seller deletes the account. The member ID stays in invite-reward key notes (no set period), the activity log (180 days) and support requests (their own period).
- Login sessions: 90 days. Sign-up and login IP hashes: a little over an hour. Support IP hashes: up to 1 hour after the last request, in memory and the Cloudflare cache only.
- Support requests: 3 years for payment and refund requests, 1 year for the rest, from the last change.
- Usage statistics: 90 days after the last connection. Activity log: 180 days.
- Requests saved by /help in your browser: until you remove them.

## Sharing, processors and transfers abroad

- Personal information is not given to third parties except where the law requires it. Discord announcements carry only what the seller writes.
- Cloudflare, Inc. (USA, dpo@cloudflare.com) runs the server and stores its data in data centers worldwide.
- Supabase Pte. Ltd. (privacy@supabase.com) stores member accounts. Database location: {{SUPABASE_REGION}}
- Lemon Squeezy handles card payments under its own privacy policy and receives the order code and, for the subscription page, the subscription number.
- The app itself connects to GitHub for updates (this can be turned off in Settings) and, for the macOS Windows-games feature, to GitHub, Valve and Apple.

## Your rights

- You can ask to see, correct or delete your information, to stop its processing, or withdraw consent: through Support in the app, /help, or the email address below. The seller answers within 10 days.
- You can delete your account under '◈ Account' → 'Delete account', turn off 'Send usage statistics' in Settings, close your requests, and remove saved requests on /help ('Remove from list').
- Sign-up is for people aged 14 or over.

## Contact

- Privacy officer: {{OWNER_NAME}}
- Email: {{OWNER_EMAIL}}
- Korean authorities for privacy complaints: Personal Information Dispute Mediation Committee 1833-6972, KISA Privacy Infringement Report Center 118, Korean National Police Agency Cyber Bureau 182.
