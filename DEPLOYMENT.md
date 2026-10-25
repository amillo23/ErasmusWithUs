# Render deployment

ErasmusWithUs deploys as two Render services:

- `erasmuswithus`: ASP.NET Core API built from `ErasmusWithUs.Api/Dockerfile`
- `erasmuswithus-web`: Vite static site built from `frontend`

## Steps

1. Push this repository to GitHub.
2. In Render, choose **New > Blueprint** and select the repository.
3. Render detects `render.yaml` and creates both services.
4. Set the secret environment values on the API service:
   - `Jwt__Key`: a random secret of at least 32 bytes
   - `Email__SmtpHost`, `Email__Username`, `Email__Password`, `Email__From`
   - Outgoing account emails use the display name `Erasmus With Us`; `Email__FromName` can override it.
5. Set `Cors__AllowedOrigins` on the API service to the exact frontend origin: `https://erasmuswithus-web.onrender.com`.
6. Set `VITE_API_BASE_URL` on the frontend service to `https://erasmuswithus.onrender.com`.
7. Redeploy both services after changing their environment settings.
8. Check the API at `/api/health`, then open the frontend URL.

## Important for production

The current free-plan configuration stores SQLite at `/tmp/erasmuswithus.db`. This storage is ephemeral and can be cleared on restart or redeploy, so account and catalogue changes are not durable. For real production use, attach a paid persistent disk or move the API to PostgreSQL. The SMTP values must be configured for account verification and password reset emails; development mode can use the returned code when SMTP is not configured.

Authentication routes are rate limited by client IP: 12 register/login attempts per minute, 4 account email/reset requests per 15 minutes, and 10 email-code attempts per 15 minutes. Community messages are limited to 30 per minute. The counters live in API memory, so a multi-instance deployment needs a shared rate-limit store as well as PostgreSQL.

The student community requires a verified account and a shared destination plus intake plan before a conversation can start. Profiles are private by default; students must opt in to appear in search. Only their chosen display name, photo, home university, study field, and introduction are shared. Messages and profile photos are stored in the SQLite database on the persistent disk.

Run `node scripts/audit-catalog-sources.mjs` to check catalogue source URLs and flag records whose review date is older than 90 days. It does not verify deadlines, grants, or eligibility requirements; those still need human review.
