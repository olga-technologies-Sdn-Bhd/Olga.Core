# CI/CD operations

The `build-test-deploy.yml` GitHub Actions workflow validates pull requests and deploys merged code using GitHub-hosted Linux runners.

## Delivery flow

| Event | Result |
| --- | --- |
| Pull request into `develop` or `main` | Restore, release build, tests, and Docker build validation |
| Push/merge to `develop` | Repeat validation, scan and publish an immutable image, deploy to `dev` |
| Push/merge to `main` | Repeat validation, scan and publish an immutable image, deploy to `prd` |
| Manual dispatch | Repeat validation and deploy the selected `dev` or `prd` environment |

Deployments use the image digest rather than a mutable tag. GitHub Actions concurrency cancels superseded development runs and serializes production runs.

## GitHub configuration

Create GitHub Environments named `dev` and `prd`. Define these variables in each environment:

| Variable | Example | Purpose |
| --- | --- | --- |
| `AZURE_CLIENT_ID` | Application/client UUID | OIDC deployment identity |
| `AZURE_TENANT_ID` | Microsoft Entra tenant UUID | Azure login |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription UUID | Azure login |
| `ACR_LOGIN_SERVER` | `acrolgadevmalaysiaweste.azurecr.io` | Image registry |
| `AZURE_RESOURCE_GROUP` | `rg-olga-dev-malaysiawest` | Container App resource group |
| `CONTAINER_APP_NAME` | `ca-olga-core-api-dev` | Deployment target |
| `WORKER_CONTAINER_APP_NAME` | `ca-olga-core-worker-dev` | Worker deployment target (no ingress) |

No long-lived Azure client secret is required. This repository uses GitHub's immutable OIDC subject format. Configure the deployment identity's federated credentials with issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, and these exact subjects:

- `dev`: `repo:Ol-gaTechnologies@306667340/Olga.Core@1358930841:environment:dev`
- `prd`: `repo:Ol-gaTechnologies@306667340/Olga.Core@1358930841:environment:prd`

The `job_workflow_ref` claim is not the federated credential subject. Grant the deployment identity only `AcrPush` on the relevant registry and the minimum Container App update permission on the target resource group or app.

Protect `prd` with required reviewers, prevent self-review, restrict it to `main`, and disable administrator bypass. Restrict `dev` to `develop`. Keep environment variables scoped to their environment.

Protect `main` and `develop` with pull requests, at least one approval, resolved conversations, dismissal of stale approvals, no force pushes/deletions, and the required `Build and test` status check. Require branches to be current before merge. Configure a ruleset if repository administrators must also follow the policy.

## Development runtime

The `dev` deployment runs in `malaysiawest` with these provisioned resources:

| Resource | Name |
| --- | --- |
| Resource group | `rg-olga-dev-malaysiawest` |
| Container registry | `acrolgadevmalaysiaweste` |
| Container Apps environment | `cae-olga-dev-devmalaysiaweste` |
| Core API Container App | `ca-olga-core-api-dev` |
| Core worker Container App | `ca-olga-core-worker-dev` |
| PostgreSQL Flexible Server | `psql-olga-devmalaysiaweste.postgres.database.azure.com` |
| PostgreSQL database | `olga_connect_dev` |
| Key Vault | `kv-olga-devmalaysiaweste` |
| Runtime managed identity | `id-olga-core-dev` |

The PostgreSQL server has public network access disabled. It uses the delegated subnet `snet-postgresql` and private DNS zone `private.postgres.database.azure.com`. The Container Apps environment uses `snet-container-apps` for VNet integration.

Configure the API container with the following exact environment-variable names:

| Name | Secret | Purpose |
| --- | --- | --- |
| `ConnectionStrings__PostgreSql` | Yes; use a Key Vault-backed Container Apps secret reference | PostgreSQL connection string using the server FQDN, database `olga_connect_dev`, port `5432`, and TLS certificate verification |
| `Mvp__DefaultMemberId` | No | Member used by anonymous MVP requests that omit `X-Member-Id`; defaults to `A123` |
| `Diagnostics__IncludeExceptionDetails` | No | Includes `stack_trace` in unhandled-error responses when enabled; root exception messages are returned in every environment |

The API intentionally does not validate caller identity in this open MVP configuration. Requests may select any member with `X-Member-Id`, so the deployment must not be treated as suitable for public or sensitive member data. Never place database credentials or tokens in GitHub variables, workflow YAML, logs, or OpenAPI documents.

Configure the worker with `ConnectionStrings__PostgreSql`, `ServiceBus__FullyQualifiedNamespace`, `ServiceBus__ManagedIdentityClientId`, `ServiceBus__IntegrationTopicName=integration-events`, and `ServiceBus__NlpCompletionSubscriptionName=core-nlp-completions`. Provision the topic with duplicate detection and the Core subscription with a SQL filter for `event_type = 'NlpMatchRequestCompleted.v1'`. Grant the Core worker identity Service Bus Data Sender on the topic and Data Receiver on only that subscription. Configure a dead-letter alert and keep at least one worker replica running.

The current registry uses the non-ABAC permission model. Grant the GitHub deployment identity `AcrPush` on `acrolgadevmalaysiaweste`, and grant the runtime managed identity only `AcrPull` on that registry. The runtime identity also needs permission to read the referenced Key Vault secrets. PostgreSQL schema creation and upgrades must run from a trusted host with network access to the private database endpoint.

The API image listens internally over HTTP on port `8080`; TLS terminates at Azure Container Apps ingress. The deployment workflow enforces target port `8080` and `allowInsecure=false` for both `dev` and `prd`, so ingress redirects public HTTP requests to HTTPS. The application does not perform HTTPS redirection or broadly trust forwarded headers, avoiding proxy redirect loops. Production responses include HSTS, and Swagger uses a relative OpenAPI URL so browser requests inherit HTTPS. Use `/health` as the process liveness endpoint and `/ready` as the readiness endpoint; `/ready` verifies PostgreSQL connectivity. Published images use the immutable commit tag `acrolgadevmalaysiaweste.azurecr.io/olga-core-api:<commit-sha>`, and deployment resolves that image to its digest.

## Runner choice

GitHub-hosted `ubuntu-latest` runners are suitable for build, test, image publication, and Azure Container Apps deployment. Use a self-hosted runner only for private-network work such as database migrations against the private PostgreSQL endpoint. Put such a runner in a dedicated runner group, use ephemeral instances, allow only selected repositories, and never run untrusted pull-request code on it.

## Rollback

Container Apps keeps revisions, and every deployment is traceable to a commit SHA and digest. Roll back by activating the last known-good revision in Azure, then revert the offending commit so source control and runtime state converge. Do not repoint or reuse an existing image tag.
