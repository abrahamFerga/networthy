# Security policy

## Supported versions

Security fixes are applied to the latest released Networthy version. Self-hosters should pin a
version for repeatability and regularly update to the newest release.

## Reporting a vulnerability

Please use GitHub's private vulnerability-reporting flow for this repository. Do not open a
public issue for a suspected vulnerability or include customer financial data, credentials,
tokens, statements, or database contents in a report.

Include the affected version, deployment mode, reproduction steps, and impact. Use synthetic
financial data in proofs of concept.

## Deployment boundary

The default Development/personal mode is intentionally keyless and is not authentication. The
provided Compose configuration binds that mode to loopback. Any LAN or internet deployment must
use Production mode, a real OIDC authority/audience, an explicit allowed host, and TLS at the
ingress.

## Container scanning note

Generic image scanners can report Go standard-library CVEs against `/usr/local/bin/gosu` in the
official Postgres image. The gosu maintainers require reachability analysis with `govulncheck`
because gosu does not invoke most flagged standard-library surfaces. See the upstream
[gosu security policy](https://github.com/tianon/gosu/blob/master/SECURITY.md). Do not suppress
findings elsewhere in the image on that basis.
