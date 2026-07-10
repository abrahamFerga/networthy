# Self-hosting Networthy

Networthy is open source first: the container image on GHCR is the **same product** the hosted
service runs — every feature, no editions, no time bombs. If you can run Docker, you can run
Networthy.

## One command

```bash
curl -LO https://github.com/abrahamFerga/networthy/releases/latest/download/docker-compose.yml
docker compose up -d
# open http://localhost:8080  (admin console at /admin)
```

That pulls `ghcr.io/abrahamferga/networthy` (no registry account needed — multi-arch, so x86
servers and ARM boxes like a Raspberry Pi both work) plus a
pgvector-enabled Postgres with a persistent volume. First start runs the database migrations
and seeds the starter category taxonomy.

Building from source instead: clone the repo and `docker compose up -d --build`.

## The two modes

| | Personal (default) | Secured |
|---|---|---|
| Audience | One household on a trusted home network | Anything reachable from the internet |
| Sign-in | Built-in household identity (no IdP) | Your OIDC provider (JWT bearer) |
| Setting | `ASPNETCORE_ENVIRONMENT=Development` (compose default) | `NETWORTHY_ENVIRONMENT=Production` + `AUTH_AUTHORITY` + `AUTH_AUDIENCE` |

**Personal mode is not authentication.** It exists so a household can be up in one command on
a LAN. The host **refuses to start** in Production without a real identity provider — there is
no way to accidentally expose the unauthenticated mode as a "production" deployment.

## AI assistant

The assistant works out of the box with the keyless Mock provider (deterministic, offline —
great for trying the product). For a real model, bring your own key; it never leaves your box:

```bash
AI_PROVIDER=OpenAI AI_MODEL=gpt-4o-mini AI_API_KEY=sk-... docker compose up -d
```

Supported providers: OpenAI, AzureOpenAI, Ollama (fully local — pair a local model with
self-hosting for a zero-cloud setup), and Mock.

Digital PDFs, CSV, and OFX/QFX statements work with nothing extra. Scanned PDFs (photos of
paper) need an OCR engine, which is a host extension point — config-driven Azure Document
Intelligence support is on the roadmap; until then scanned statements report honestly that no
OCR engine is configured.

Uploaded files live in the `networthy-files` volume by default; point `FILES_PROVIDER=AzureBlob`
plus `FILES_AZURE_CONNECTION` at a storage account to keep them in Azure Blob instead.

## Day-2 operations

- **Upgrade**: `docker compose pull && docker compose up -d` — migrations run on start.
- **Pin a version**: `NETWORTHY_VERSION=0.2.0 docker compose up -d` (releases are tagged).
- **Back up**: the `networthy-data` volume is the entire state; `pg_dump` works as usual.
- **Change the port**: `NETWORTHY_PORT=9090 docker compose up -d`.
- **Postgres password**: `POSTGRES_PASSWORD=... docker compose up -d` (set before first start).

## Why there's also a paid option

Self-hosting is and stays the first-class path. The [hosted service](HOSTED.md) exists for
people who don't want to run a server: a small fee covers the infrastructure and the AI tokens
— the same open-source code, operated for you.
