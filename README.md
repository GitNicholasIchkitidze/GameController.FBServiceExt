# GameController.FBServiceExt

Dedicated solution for the next-generation Messenger voting pipeline.

## Structure

- `GameController.FBServiceExt`: ASP.NET Core ingress host
- `GameController.FBServiceExt.Worker`: background worker host
- `GameController.FBServiceExt.Application`: orchestration and use cases
- `GameController.FBServiceExt.Domain`: state machine and business rules
- `GameController.FBServiceExt.Infrastructure`: RabbitMQ, Redis, SQL, Facebook integrations
- `GameController.FBServiceExt.Tests`: test project

## Agreed goals

- Industrial-grade, scalable, secure, and reliable Messenger voting pipeline
- Sustained load target: `200000` messages/hour
- Peak load target: `350` messages/second
- Average webhook size: `1-3` messaging events per request
- Webhook ingress returns `200 OK` only after durable RabbitMQ publish confirm
- Pipeline: raw ingress queue -> normalizer -> normalized event queue -> processor
- Runtime state: Redis + SQL
- Day 1 durable history: normalized events + accepted votes
- Outbound Facebook messaging and downstream realtime publish stay for a later phase

## Composition roots

- API host loads only ingress concerns: `MetaWebhook`, `WebhookIngress`, `RabbitMq`, logging
- Worker host loads only processing concerns: `VotingWorkflow`, `Candidates`, `RabbitMq`, `Redis`, `SqlStorage`, logging
- `RabbitMq` intentionally exists in both hosts because both processes depend on the broker

## Logging

- Host and worker use a standard `Serilog + Serilog.Sinks.Graylog` stack
- Configure the `Serilog` section in each appsettings file
- Default local endpoint: `127.0.0.1:12201`
- Console logging remains enabled alongside Graylog forwarding

## Local dev infrastructure

`docker-compose.yml` now brings up only the infrastructure that benefits from containerized isolation for local development:

- RabbitMQ
- Redis
- Graylog
- Graylog internal dependencies: MongoDB and OpenSearch

SQL Server is expected to already exist on the host machine. `FBServiceExt` uses `EF Core` against that local SQL Server instance.

Default local ports for this solution are:

- RabbitMQ AMQP: `5672`
- RabbitMQ management: `15672`
- Redis: `6380`
- Graylog web: `9000`
- Graylog GELF UDP: `12201`

### First-time setup

1. Copy `.env.example` to `.env` if you want to customize the local stack
2. Change at least `RABBITMQ_DEFAULT_PASS`, `GRAYLOG_PASSWORD_SECRET`, and `OPENSEARCH_INITIAL_ADMIN_PASSWORD`
3. Start the stack with `docker compose up -d`
4. Ensure your local SQL Server instance is running and reachable from the host
5. Open RabbitMQ management at `http://127.0.0.1:15672`
6. Open Graylog at `http://127.0.0.1:9000`

### Graylog login

On the very first Graylog startup, the service opens a setup interface and prints a one-time admin password into the container log.

- Username: `admin`
- Get the current one-time password with `docker logs fbserviceext-graylog`
- After setup is completed, continue with the credentials you configure in the UI
- OpenSearch also uses an internal admin password from `.env` for container bootstrap

### Graylog input

After the initial Graylog setup is completed, create a global `GELF UDP` input on port `12201`. The application logging stack already targets `127.0.0.1:12201`.

### Running the app against the local stack

When the API and Worker run on the host machine:

- RabbitMQ stays on `localhost:5672`
- Redis stays on `localhost:6380`
- SQL Server is expected on your local machine, not in Docker

The committed `appsettings.Development.json` for the Worker already points to:

- `Redis`: `localhost:6380`
- `SQL Server`: `Server=localhost;Database=GameControllerFBServiceExt;Trusted_Connection=True;TrustServerCertificate=True;`

PowerShell example if you want to override credentials or server name:

```powershell
$env:RabbitMq__UserName = 'fbserviceext'
$env:RabbitMq__Password = 'DevOnly_RabbitMq2026!'
$env:Redis__ConnectionString = 'localhost:6380'
$env:SqlStorage__ConnectionString = 'Server=localhost;Database=GameControllerFBServiceExt;Trusted_Connection=True;TrustServerCertificate=True;'
```

### Notes

- The Worker creates the `GameControllerFBServiceExt` database schema through `EF Core EnsureCreated` on startup if the database/user permissions allow it.
- If your local SQL Server is a named instance or uses SQL authentication, override `SqlStorage__ConnectionString` accordingly.
- The Graylog stack needs more memory than RabbitMQ and Redis alone. On Docker Desktop, reserve at least `4 GB` RAM.
- `docker compose down` stops the stack. `docker compose down -v` also removes the persisted Docker volumes for RabbitMQ, Redis, Graylog, MongoDB, and OpenSearch.
## Performance testing

A local K6-based ingress ACK harness lives under [`perf`](./perf).

Typical run:

```powershell
Set-Location 'E:\GAME SHOW Project\GameShowControlSolution\GameController.FBServiceExtSolution'
.\perf\run-k6-ack-250rps.ps1
```

The harness validates:

- sustained webhook arrival rate
- fast `200 OK` ACK latency
- low error rate and zero dropped iterations
- RabbitMQ backlog drain after the run

See [`perf/README.md`](./perf/README.md) for parameters and success criteria.

