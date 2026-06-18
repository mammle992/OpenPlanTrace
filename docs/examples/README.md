# Output Examples

These files are generated from the public `samples/golden/semantic-smoke.dxf`
fixture and are safe to keep in the repository.

- `openplantrace.scan.example.json` is a full OpenPlanTrace scan artifact using
  the current scan schema.
- `openplantrace.geojson.example.json` is a page-coordinate GeoJSON feature
  collection. Coordinates are OpenPlanTrace drawing units, not WGS84.

Regenerate them with:

```powershell
dotnet run --project tools/OpenPlanTrace.Cli/OpenPlanTrace.Cli.csproj --configuration Release -- scan samples/golden/semantic-smoke.dxf --json docs/examples/openplantrace.scan.example.json --geojson docs/examples/openplantrace.geojson.example.json
```
