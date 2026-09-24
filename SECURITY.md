# Security Policy

This alpha has no authentication. Bind to loopback or use a trusted network and
an authenticated, rate-limited reverse proxy. Any caller can consume CPU, network
bandwidth and storage. There is no claim of safe anonymous Internet deployment.

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
