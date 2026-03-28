# Performance Harness

This folder contains the local ingress ACK and stress-test harness for `GameController.FBServiceExt`.

## Goal

Validate that the webhook API can accept sustained Messenger webhook load, return `200 OK` fast enough, and let the worker drain RabbitMQ backlog without lagging or leaving queue residue.

## What the harness checks

- API readiness before the run
- sustained constant-arrival-rate K6 load against `POST /api/facebook/webhooks`
- `http_req_duration` p95/p99 thresholds
- low HTTP failure rate
- zero dropped iterations
- high `200 OK` rate
- RabbitMQ queue drain after the run (`raw.ingress` and `normalized.events`)
- optional live RabbitMQ queue/rate monitoring during a stress run

## Files

- `k6/scenarios/ack-250rps.js`: main K6 scenario
- `k6/lib/messengerWebhook.js`: realistic Messenger webhook payload builder
- `run-k6-ack-250rps.ps1`: single-run ACK harness with queue drain verification
- `run-k6-ack-step-ladder.ps1`: staged `50 -> 250 msg/sec` diagnostic runner
- `monitor-rabbitmq.ps1`: live RabbitMQ management API sampler for queue depth and rates
- `run-k6-ack-stress-monitored.ps1`: monitored stress wrapper that runs K6 and RabbitMQ sampling together

## RabbitMQ management UI

- URL: `http://127.0.0.1:15672`
- Username: `fbserviceext`
- Password: `DevOnly_RabbitMq2026!`

## Default assumptions

- API is running locally on `http://localhost:5277`
- Worker is running
- RabbitMQ management API is available on `http://localhost:15672`
- In `Development`, signature validation is disabled, so `AppSecret` is optional
- If signature validation is enabled, pass `-AppSecret` to the runner

## Performance environment hosts

For heavy stress tests, do not use the normal `Development` runtime. Start both hosts with `Performance` environment so debug-level logs and dev-only noise are suppressed.

API:

```powershell
$env:DOTNET_ENVIRONMENT='Performance'
$env:ASPNETCORE_ENVIRONMENT='Performance'
& 'E:\GAME SHOW Project\GameShowControlSolution\GameController.FBServiceExtSolution\src\GameController.FBServiceExt\bin\Release\net8.0\GameController.FBServiceExt.exe' --urls 'http://127.0.0.1:5277'
```

Worker:

```powershell
$env:DOTNET_ENVIRONMENT='Performance'
$env:ASPNETCORE_ENVIRONMENT='Performance'
& 'E:\GAME SHOW Project\GameShowControlSolution\GameController.FBServiceExtSolution\src\GameController.FBServiceExt.Worker\bin\Release\net8.0\GameController.FBServiceExt.Worker.exe'
```

Use this mode for `400/sec` and above, or for any run longer than `60s`.
## Typical ACK run

```powershell
Set-Location 'E:\GAME SHOW Project\GameShowControlSolution\GameController.FBServiceExtSolution'
.\perf\run-k6-ack-250rps.ps1
```

## Example with explicit parameters

```powershell
.\perf\run-k6-ack-250rps.ps1 `
  -BaseUrl 'http://localhost:5277' `
  -TargetMessagesPerSecond 250 `
  -EventsPerRequest 1 `
  -Duration '90s' `
  -AckP95Ms 250 `
  -AckP99Ms 500
```

## If you want 250 messaging events/sec with batched webhook requests

For example, if each request carries `3` messaging events:

```powershell
.\perf\run-k6-ack-250rps.ps1 -TargetMessagesPerSecond 250 -EventsPerRequest 3
```

The K6 scenario automatically converts this to the required request arrival rate.

## Step ladder

Use this when you want to find the first failing throughput step before running the full target scenario:

```powershell
.\perf\run-k6-ack-step-ladder.ps1 -BaseUrl 'http://localhost:5277' -Steps @(50,100,150,200,250) -Duration '20s'
```

This writes an aggregated ladder report in addition to the per-step K6 artifacts.

## Live RabbitMQ monitor only

Use this when you want queue depth/rates without starting a stress run from the wrapper:

```powershell
.\perf\monitor-rabbitmq.ps1 -DurationSeconds 180 -SampleIntervalMilliseconds 1000
```

Artifacts produced:

- `rabbitmq-samples.csv`
- `rabbitmq-samples.json`
- `rabbitmq-summary.json`

Captured fields per sample:

- `Messages`
- `MessagesReady`
- `MessagesUnacknowledged`
- `Consumers`
- `PublishRate`
- `DeliverGetRate`
- `AckRate`
- `RedeliverRate`
- `MemoryBytes`

## Heavier monitored stress run

Recommended first heavier run:

```powershell
.\perf\run-k6-ack-stress-monitored.ps1 `
  -BaseUrl 'http://127.0.0.1:5277' `
  -TargetMessagesPerSecond 400 `
  -EventsPerRequest 1 `
  -Duration '300s' `
  -AckP95Ms 300 `
  -AckP99Ms 600
```

Recommended heavier batched run:

```powershell
.\perf\run-k6-ack-stress-monitored.ps1 `
  -BaseUrl 'http://127.0.0.1:5277' `
  -TargetMessagesPerSecond 500 `
  -EventsPerRequest 3 `
  -Duration '300s' `
  -AckP95Ms 300 `
  -AckP99Ms 600
```

This wrapper:

- starts RabbitMQ monitoring in parallel
- runs the normal ACK harness
- keeps the normal `k6-summary.json` and `queue-drain.json`
- adds `k6-summary.normalized.json` and `k6-summary.normalized.txt` so pass/fail is explicit and not dependent on raw K6 threshold serialization
- adds RabbitMQ monitoring artifacts under a sibling `rabbitmq-monitor` folder

## Artifacts

Each ACK run writes files into:

- `artifacts/perf/ack-250rps-<timestamp>/k6-summary.json`
- `artifacts/perf/ack-250rps-<timestamp>/queue-drain.json`

Each monitored stress run writes files into:

- `artifacts/perf/ack-stress-monitored-<timestamp>/rabbitmq-monitor/rabbitmq-samples.csv`
- `artifacts/perf/ack-stress-monitored-<timestamp>/rabbitmq-monitor/rabbitmq-samples.json`
- `artifacts/perf/ack-stress-monitored-<timestamp>/rabbitmq-monitor/rabbitmq-summary.json`

## Recommended success criteria

- `http_req_duration p95 < 250 ms` for the validated 250 msg/sec baseline
- `http_req_duration p99 < 500 ms` for the validated 250 msg/sec baseline
- `http_req_failed rate < 1%`
- `dropped_iterations == 0`
- `webhook_status_200 rate > 99%`
- RabbitMQ queues return to `0` after the run
- during monitored stress runs, `MessagesReady` should not grow without recovering
- during monitored stress runs, `MessagesUnacknowledged` should stay bounded and drain after load stops


