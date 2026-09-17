# build-analytics

Azure DevOps build/export + spreadsheet tool.

## Config

Default config file:

- `build-analytics.config.json`

Config precedence:

1. config file
2. environment variables
3. CLI arguments

You can override config location with:

- `--config <path>`
- `BUILD_ANALYTICS_CONFIG=<path>`

## Commands

### export

Exports Azure DevOps build run data and writes raw `run.json` files.

```bash
dotnet run --project /workspace/repos/build-analytics -- export
```

Useful options:

- `--output-root <path>`
- `--definition-id <ids>`
- `--definition-name <names>`
- `--min-time <iso8601>`
- `--max-time <iso8601>`
- `--throttle-limit <n>`
- `--max-runs <n>`
- `--count-only`

Example:

```bash
dotnet run --project /workspace/repos/build-analytics -- export \
  --output-root /workspace/build-analytics-full
```

### spreadsheet

Builds an Excel workbook from exported run JSON.

```bash
dotnet run --project /workspace/repos/build-analytics -- spreadsheet
```

Useful options:

- `--input-root <path>`
- `--output <file.xlsx>`
- `--pool-id <ids>`
- `--exclude-reason <reason>`
- `--max-queue-wait-seconds <sec>`

Example:

```bash
dotnet run --project /workspace/repos/build-analytics -- spreadsheet \
  --input-root /workspace/build-analytics-full \
  --output /workspace/build-analytics-full.xlsx \
  --pool-id 9 \
  --exclude-reason schedule \
  --max-queue-wait-seconds 7200
```

## Environment variables

Export mode:

- `AZDO_ORG_URL`
- `AZDO_PROJECT`
- `AZDO_PAT`
- `BUILD_ANALYTICS_OUTPUT_ROOT`
- `BUILD_ANALYTICS_DEFINITION_IDS`
- `BUILD_ANALYTICS_DEFINITION_NAMES`
- `BUILD_ANALYTICS_MIN_TIME`
- `BUILD_ANALYTICS_MAX_TIME`
- `BUILD_ANALYTICS_THROTTLE_LIMIT`
- `BUILD_ANALYTICS_MAX_RUNS`
- `BUILD_ANALYTICS_COUNT_ONLY`

Spreadsheet mode:

- `BUILD_ANALYTICS_INPUT_ROOT`
- `BUILD_ANALYTICS_OUTPUT_PATH`
- `BUILD_ANALYTICS_POOL_IDS`
- `BUILD_ANALYTICS_EXCLUDE_REASONS`
- `BUILD_ANALYTICS_MAX_QUEUE_WAIT_SECONDS`

## Config file example

```json
{
  "organizationUrl": "https://dev.azure.com/AGLCDevOps",
  "project": "AGLC",
  "pat": "<pat>",
  "outputRoot": "/workspace/build-analytics-full",
  "inputRoot": "/workspace/build-analytics-full",
  "outputPath": "/workspace/build-analytics-full.xlsx",
  "poolIds": [9],
  "excludeReasons": ["schedule"],
  "maxQueueWaitSeconds": 7200
}
```

## Notes

- `export` writes raw run data to `OUTPUT_ROOT/runs/<runId>__<name>/run.json`
- `spreadsheet` reads the exported raw files and generates:
  - `Overview`
  - `Runs`
  - `Monthly`
