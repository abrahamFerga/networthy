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

That pulls `ghcr.io/abrahamferga/networthy` and its security-updated
`ghcr.io/abrahamferga/networthy-postgres` companion (no registry account needed — both are
multi-arch, so x86 servers and ARM boxes like a Raspberry Pi work) with a persistent database
volume. First start runs the database migrations and seeds the starter category taxonomy.

Building from source instead: clone the repo and `docker compose up -d --build`.

## The two modes

| | Personal (default) | Secured |
|---|---|---|
| Audience | One household on the Docker host | Anything reachable from a LAN or the internet |
| Sign-in | Built-in household identity (no IdP) | Your OIDC provider (JWT bearer) |
| Setting | `ASPNETCORE_ENVIRONMENT=Development` (compose default) | `NETWORTHY_ENVIRONMENT=Production` + `AUTH_AUTHORITY` + `AUTH_AUDIENCE` |

**Personal mode is not authentication.** The compose file therefore binds it to `127.0.0.1`
by default. The host **refuses to start** in Production without a real identity provider.
For a secured LAN or internet deployment, opt in to a non-loopback binding as well as OIDC:

```bash
NETWORTHY_ENVIRONMENT=Production NETWORTHY_BIND_ADDRESS=0.0.0.0 \
  ALLOWED_HOSTS=finance.example.com AUTH_AUTHORITY=https://<idp> \
  AUTH_AUDIENCE=<audience> docker compose up -d
```

## AI assistant

The assistant works out of the box with the keyless Mock provider (deterministic, offline —
great for trying the product). For a real model, start Networthy and configure the tenant's
provider/model/key under **Admin → AI Settings**. The key is stored write-only in the secret vault.

Supported providers: OpenAI, AzureOpenAI, Ollama (fully local — pair a local model with
self-hosting for a zero-cloud setup), and Mock.

Digital PDFs, CSV, and OFX/QFX statements work with nothing extra. Scanned PDFs (photos of
paper) need an OCR engine — bring an [Azure Document Intelligence](https://learn.microsoft.com/azure/ai-services/document-intelligence/)
resource and set three variables:

```bash
OCR_PROVIDER=AzureDocumentIntelligence OCR_ENDPOINT=https://<resource>.cognitiveservices.azure.com \
  OCR_API_KEY=... docker compose up -d
```

Scanned pages then go through the prebuilt-read model of your own Azure resource (your documents
never touch anyone else's account). Without these, scanned statements report honestly that no
OCR engine is configured.

Uploaded files live in the `networthy-files` volume by default; point `FILES_PROVIDER=AzureBlob`
plus `FILES_AZURE_CONNECTION` at a storage account to keep them in Azure Blob instead.

## Day-2 operations

- **Upgrade**: `docker compose pull && docker compose up -d` — migrations run on start.
- **Pin a version**: `NETWORTHY_VERSION=0.2.0 docker compose up -d` (releases are tagged).
- **Back up**: the `networthy-data` volume is the entire state; `pg_dump` works as usual.
- **Change the port**: `NETWORTHY_PORT=9090 docker compose up -d`.
- **Listen beyond localhost**: set `NETWORTHY_BIND_ADDRESS=0.0.0.0` only with secured
  Production mode, `ALLOWED_HOSTS`, and OIDC configured as above.
- **Postgres password**: `POSTGRES_PASSWORD=... docker compose up -d` (set before first start).

## Why there's also a paid option

Self-hosting is and stays the first-class path. The [hosted service](HOSTED.md) exists for
people who don't want to run a server: a small fee covers the infrastructure and the AI tokens
— the same open-source code, operated for you.
