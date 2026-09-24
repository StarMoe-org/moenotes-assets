# Security Policy

Administrative HTTP routes require a Bearer key configured in `MOENOTES_API_KEY`:
all mutations, task queries and storage statistics. Missing configuration disables
these routes. Public browsing and published files remain anonymous. Use HTTPS,
keep the key in server-side secret storage, and restart to rotate it. No user
accounts or per-user permissions are provided. Use a rate-limited reverse proxy
for public reads and do not embed the administrative key in a public frontend.

Keep the data directory private to the service UID. Database and filesystem
contents are trusted local state; do not allow other users to replace indexed
files, SQLite files, or working directories while the service runs. Configure a
container memory limit, filesystem quota and PID limit as additional boundaries.
Worker resource limits and separate processes are not a security sandbox.

The worker command is an internal process interface, not exposed
through HTTP. Configure CDN roots and executable paths only from trusted sources.
Downloads do not follow redirects or inherit proxy variables. Export names are
service-generated; asset labels are untrusted metadata, not filesystem paths.

Malformed assets can still expose defects in third-party parsers or native codecs.
Keep dependencies and the runtime image patched. No complete dependency-security
audit or comprehensive fuzzing campaign is claimed for this prerelease.

Please report vulnerabilities privately using this repository's GitHub security
advisories. Do not include player credentials or copyrighted asset payloads in
public issues. Describe versions and reproduction steps using synthetic fixtures
where possible. Only the latest prerelease is maintained at present.
