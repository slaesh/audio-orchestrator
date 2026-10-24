# System Agents & Commands

## General Programming Rules

- **Prefer Early Exits**: Always use early exits (`if (!test) return;`) instead of deeply nested `if/else` blocks. This keeps functions flat and readable.
- **Prefer Type Inference**: Do NOT explicitly type the arguments of array functions (like `map`, `find`, `filter`) if the array is already typed. Let TypeScript infer the types automatically (e.g., `list.map(e => ...)` instead of `list.map((e: Dto) => ...)`).
- **Strong Typing over Strings (Enums)**: Do NOT use raw strings for fixed value sets (e.g., `string? sourceType`). Always define a strongly typed C# enum decorated with:
  ```csharp
  [TranspilationSource]
  [JsonConverter(typeof(JsonStringEnumConverter))]
  public enum MyEnum
  {
      [JsonStringEnumMemberName("foo")]
      Foo,
      [JsonStringEnumMemberName("bar")]
      Bar
  }
  ```
  This ensures Tapper transpiles proper TypeScript string-union types (e.g. `"foo" | "bar"`) and System.Text.Json serializes them matching the API contract.
- **Enum Persistence in EF Core**: When persisting enums to SQLite, always configure a string value converter in `OrchestratorDbContext` (e.g. `.HasConversion(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<MyEnum>(v, true))`) to preserve backward compatibility with existing SQLite `TEXT` columns.
- **Database Migrations**: ALWAYS use Entity Framework migrations for schema changes (`dotnet ef migrations add ...`). NEVER manually edit the production/shared database schema.

## Architecture & Monorepo Structure

- **Backend (`apps/orchestrator/`)**: .NET 10 ASP.NET Core Web API.
- **Frontend (`apps/orchestrator/ClientApp/`)**: Angular 21 SPA.
- **Spotify Node (`librespot-server`)**: External standalone Rust daemon ([`slaesh/librespot-server`](https://github.com/slaesh/librespot-server)) pulled as `ghcr.io/slaesh/librespot-server:latest`.
- **Control Plane**: The C# Orchestrator uses `Docker.DotNet` (via `/var/run/docker.sock`) to dynamically spawn/kill `orch_spotify_*` and `orch_radio_*` containers on the host. 
- **State**: Persistent state is kept in SQLite at `storage/orchestrator/orchestrator.db`.
- **Audio Pipes**: Containers communicate with `snapserver` by writing raw PCM to shared named pipes in `/tmp/audio_fifos`.
- **Persistence**: Spotify credentials are persisted in a shared Docker volume `librespot_cache` mounted at `/var/cache/librespot`.

## Development Commands & Quirks

- **Build Backend**: `dotnet build` from root or `orchestrator/`.
- **Run Tests (Backend)**: `dotnet test apps/orchestrator-tests` from the repository root.
- **Run/Test Application**: For full stack testing, use `docker compose up -d`. For active development, see the **Hybrid Development** section below.
- **SignalR Codegen**: The `TypedSignalR` TypeScript client and Tapper DTOs (`ClientApp/src/app/core/signalr/`) are **auto-generated** during every `dotnet build` of the AudioOrchestrator project (via the `dotnet-tsrts` tool).
- **OpenAPI Client Codegen**: We no longer use Swagger runtime generation. The OpenAPI spec is **build-time generated** (`orchestrator/AudioOrchestrator.json`) via `Microsoft.Extensions.ApiDescription.Server`.
  To regenerate the Angular API client (`ClientApp/src/app/generated/`) after backend API changes, run:
  ```bash
  npm run generate-api
  ```
- **Spotify Node Client Codegen**: Regenerate the C# client via:
  ```bash
  npm run generate-spotify-node-client
  ```
- **DTO Consistency**: SignalR and OpenAPI clients **MUST** share the same DTO types. The Tapper-generated contracts in `ClientApp/src/app/core/signalr/AudioOrchestrator.Contracts.ts` are the source of truth. Whenever adding a new DTO or Enum:
  1. Decorate the C# record/enum with `[TranspilationSource]`.
  2. Add the type to `openapitools.json` under both `typeMappings` (`"MyDto": "MyDto"`) and `importMappings` (`"MyDto": "@contracts"`).
  3. Run `npm run generate-api` to generate both Tapper contracts and the OpenAPI Angular client.
  4. Ensure `ClientApp/src/app/generated/model/models.ts` (and `@api`) exports the Tapper DTOs instead of OpenAPI-generated duplicates to maintain type compatibility across the application.

## Service Commands (Docker)

- **Start Local Deployment**: `docker compose up -d` (from repository root)
- View Orchestrator logs: `docker compose logs -f orchestrator`
- Stop everything: `docker compose down`

## Hybrid Development

For an optimal local developer experience (hot-reloading for both backend and frontend):
1. Start infrastructure only: `docker compose up -d snapserver snapclient-local`
2. Start the backend with hot-reload: `cd apps/orchestrator && dotnet watch run`
3. Start the frontend with hot-reload (proxied to the backend): `cd apps/orchestrator/ClientApp && npm run start`

## Troubleshooting

- **Snapclients not discovering Snapserver**: Ensure `avahi-daemon` is running on the host (required for mDNS discovery via `/var/run/dbus/system_bus_socket`). Verify with `docker compose logs snapserver | grep -i avahi`. Note that when migrating servers or changing host IPs, existing `snapclient` services cache the old IP and must be restarted (`sudo systemctl restart snapclient`). Ensure firewall allows UDP 5353 and TCP 1704/1705.
- **LibreSpot streams not switching**: Check Webhook logs on Orchestrator via `docker logs orchestrator`.
- **Snapclient connection issues**: Verify host IP bindings. The Orchestrator natively discovers `localhost:1780`.
- **SELinux / Fedora Permission Denied**: Ensure `security_opt: [label:disable]` is set in `docker-compose.yml` and `chown -R 1000:1000 storage` has been run on the host (`/tmp/audio_fifos` is managed automatically by the Orchestrator).
