# Security Policy

ZenVizor is a local-only Windows desktop application. It has no user accounts,
no authentication, and (by design) makes no network connections of its own —
so the usual "server / API / auth" threat model does not apply.

## Realistic threat surface

Because there is no network or account surface, the primary realistic
vulnerability class is **the handling of malicious or malformed input** —
that is, untrusted files the app opens, parses, or imports (for example,
exported report files, configuration files, or any capture data loaded from
disk). Reports concerning file-parsing crashes, memory safety issues, or
privilege boundary violations between the elevated service and the
non-elevated UI are in scope. Reports about network endpoints, login flows,
or remote APIs are not applicable to this project.

## Reporting a Vulnerability

Please report suspected vulnerabilities **privately** using GitHub's private
vulnerability reporting feature:

- Go to the repository's **Security** tab
- Click **Report a vulnerability**

Do **not** open a public GitHub issue for security reports, and please do not
disclose the issue publicly (blog posts, social media, forums, etc.) until a
fix has been released.

If you cannot use GitHub's private reporting for some reason, contact
`admin@zenvizor.com` instead.

## Response expectations

ZenVizor is maintained by a single person on a best-effort basis. There is no
guaranteed response-time SLA. In practice:

- I will try to acknowledge a valid report within about a week.
- Triage, fix development, and release timing depend on severity and on my
  availability — please be patient.
- I will let you know when a fix is released and are happy to credit you in
  the release notes if you would like.

## Supported Versions

Only the **latest released version** of ZenVizor receives security fixes.
Older versions are unsupported — if you are running one, please upgrade before
reporting, and please confirm the issue still reproduces on the current
release.
