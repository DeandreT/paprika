# Running the paprika image

The release image is a single Linux/amd64 process: .NET 10 serves the Angular UI, PDF.js worker and API on HTTP port **8080**. It runs as the .NET image's non-root `app` user, in shared mode with Simulation enabled. No provider credentials are needed to use Simulation. Supply an explicitly authorized user to review packets; there is no built-in account or password.

## Build and verify locally

From the checkout, with Docker and Python 3:

```sh
docker build --platform linux/amd64 -t paprika:local .
bash scripts/check-container.sh paprika:local
```

The smoke test creates a disposable volume and administrator credential and publishes a random loopback port. It uploads a synthetic PDF, rasterizes a page with PDFium and Skia, approves a review, downloads the document and ZIP, replaces the container, and verifies the saved packet and original bytes. It cleans up its containers and volume. A Windows development/package install continues to work independently.

## Runtime contract

- **Network:** Set `paprika__Hosting__AllowedHosts__0` to the public hostname (no scheme) and `paprika__Hosting__AllowedOrigins__0` to the exact HTTPS browser origin when TLS terminates at ingress. For a different container port, change `paprika__Hosting__Url` **and** the service target/probe port together. Keep `paprika__Operations__Mode=shared` for networked deployments.
- **Access:** Configure server-side user names, roles and workflow assignments as described in [shared review operations](operations-platform.md). Supply bearer credentials through runtime secret references, never image layers. Upstream SSO still requires a trusted authentication integration. The `X-paprika` header is a request marker, not authentication.
- **Probes:** `GET /health/live` checks the listener. `GET /health/ready` checks that the packet database is readable (200 or 503). Both return only a status and support platform probes addressed to a pod IP. `/api/health` and normal packet access keep their existing authentication and host checks. Readiness does not check downstream providers.
- **State:** Mount `/data` on writable **single-writer, SQLite-WAL-compatible local storage**. A Docker named volume works for development. Container-local ephemeral storage is suitable only for disposable tests. Do not mount the SQLite directory on SMB/NFS, overlap old/new writers during rollout, or scale out the current storage profile. Use the [offline backup/restore procedure](recovery.md). For durable browser sessions across replacements, configure persistent, protected ASP.NET Data Protection keys separately.
- **Configuration:** `paprika__DataDirectory`, `paprika__Mode`, `paprika__Hosting__*` and `paprika__Operations__*` are runtime inputs. Existing catalogs pin their workflows; changing the mode variable does not convert them. Keep exact hostnames, identities, provider credentials, infrastructure and environment-specific policies in the deployment repository.

## Image release and promotion

Pull requests and `main` build and smoke-test the image. A published GitHub Release with a version tag such as `v1.2.3` runs the same gate, then pushes **that tested image** to `ghcr.io/<lowercase-owner>/<lowercase-repository>` under the release tag and `sha-<full-commit>`. The release workflow records the immutable `repository@sha256:…` reference in the job summary and `image-reference` artifact. Repository owners control package visibility and read access.

Consumers should mirror/import the selected digest into their registry and verify the destination manifest digest. Promote that same digest through environments without rebuilding. If a host template only supports tags, bind a version or full-commit tag once in the destination registry, lock it, and check its resolved digest before rollout. A registry import is distinct from deploying an environment.
