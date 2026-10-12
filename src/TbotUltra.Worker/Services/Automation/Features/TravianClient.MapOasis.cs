using TbotUltra.Worker.Services.Automation;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient : IMapOasisAreaReader
{
    internal async Task PrepareMapOasisScanAsync(CancellationToken cancellationToken)
    {
        await LoginAsync(cancellationToken);
        await GotoAsync(Paths.Map, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);
    }

    public Task<string> ReadMapAreaAsync(int x, int y, CancellationToken cancellationToken)
        => ReadMapAreaAsync(x, y, zoomLevel: 3, cancellationToken);

    /// <summary>
    /// Lists unoccupied valleys within <paramref name="radius"/> fields of a centre, for choosing where
    /// to settle. Uses the same map API areas as the oasis scan, at the first zoom that returns tiles.
    /// </summary>
    public async Task<IReadOnlyList<FreeValley>> FindFreeValleysAsync(int centerX, int centerY, int radius, CancellationToken cancellationToken)
    {
        await PrepareMapOasisScanAsync(cancellationToken);
        radius = Math.Clamp(radius, 1, 40);

        // Width and height of the area one map API call returns at each zoom level.
        (int Zoom, int Width, int Height)[] zooms = [(3, 31, 31), (2, 21, 17), (1, 11, 9)];
        var found = new Dictionary<(int X, int Y), FreeValley>();
        foreach (var (zoom, width, height) in zooms)
        {
            var firstArea = await ReadMapAreaAsync(centerX, centerY, zoom, cancellationToken);
            if (MapOasisApiParser.IsRegionOverlay(firstArea))
            {
                continue;
            }

            var areas = 0;
            for (var y = centerY - radius + (height / 2); y - (height / 2) <= centerY + radius; y += height)
            {
                for (var x = centerX - radius + (width / 2); x - (width / 2) <= centerX + radius; x += width)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var json = areas == 0 && x == centerX && y == centerY ? firstArea : await ReadMapAreaAsync(x, y, zoom, cancellationToken);
                    areas++;
                    foreach (var valley in MapOasisApiParser.ParseFreeValleys(json))
                    {
                        if (Math.Abs(valley.X - centerX) <= radius && Math.Abs(valley.Y - centerY) <= radius)
                        {
                            found[(valley.X, valley.Y)] = valley;
                        }
                    }

                    await Task.Delay(Random.Shared.Next(350, 800), cancellationToken); // Human-like map panning.
                }
            }

            Notify($"[settle] read {areas} map area(s) at zoom {zoom}; {found.Count} free valleys within {radius} of ({centerX}|{centerY}).");
            break;
        }

        return found.Values.ToList();
    }

    /// <summary>Reads the account's culture points from the game shell's embedded state.</summary>
    public async Task<CulturePoints?> ReadCulturePointsAsync(CancellationToken cancellationToken)
    {
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var json = await _page.EvaluateAsync<string?>(
                """
                () => {
                  const match = (document.documentElement?.innerHTML || '').match(/"culturalPointsOverview"\s*:\s*(\{[^}]*\})/);
                  return match ? match[1] : null;
                }
                """).WaitAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var document = System.Text.Json.JsonDocument.Parse(json);
                var root = document.RootElement;
                int Read(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number ? value.GetInt32() : 0;
                return new CulturePoints(
                    Read("usedSlots"), Read("maxControllableVillages"), Read("cpProducedForNextSlot"), Read("cpNeededForNextSlot"), Read("cpProductionTotal"));
            }

            // Not every page embeds the village-list state; the village overview always does.
            await GotoAsync(Paths.Resources, cancellationToken);
            await WaitForPageReadyAsync(cancellationToken);
        }

        return null;
    }

    public async Task<MapOasisApiCapture> CaptureCurrentMapAreaAsync(CancellationToken cancellationToken)
    {
        if (!IsCurrentUrlForPath(Paths.Map))
        {
            throw new InvalidOperationException("Open the map and center it on the area to capture first.");
        }

        var xText = await _page.Locator("#xCoordInputMap").InputValueAsync().WaitAsync(cancellationToken);
        var yText = await _page.Locator("#yCoordInputMap").InputValueAsync().WaitAsync(cancellationToken);
        if (!int.TryParse(xText, out var x) || !int.TryParse(yText, out var y))
        {
            throw new InvalidOperationException("Could not read the current map coordinates.");
        }

        var zoomLevel = 1;
        if (Uri.TryCreate(_page.Url, UriKind.Absolute, out var uri))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2
                    && string.Equals(parts[0], "zoom", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(Uri.UnescapeDataString(parts[1]), out var parsedZoom)
                    && parsedZoom is >= 1 and <= 4)
                {
                    zoomLevel = parsedZoom;
                    break;
                }
            }
        }

        var json = await ReadMapAreaAsync(x, y, zoomLevel, cancellationToken);
        Notify($"[map-oasis] captured map API area center=({x}|{y}) zoom={zoomLevel}; chars={json.Length}.");
        return new MapOasisApiCapture(_page.Url, x, y, zoomLevel, json);
    }

    public Task<string> ReadMapAreaAsync(int x, int y, int zoomLevel, CancellationToken cancellationToken)
    {
        return _page.EvaluateAsync<string>(
            """
            async ({ x, y, zoomLevel }) => {
                const response = await fetch('/api/v1/map/position', {
                    method: 'POST', credentials: 'same-origin',
                    headers: { 'content-type': 'application/json' },
                    body: JSON.stringify({ data: { x, y, zoomLevel, ignorePositions: [] } })
                });
                const text = await response.text();
                if (!response.ok) throw new Error(`HTTP ${response.status}: ${text.slice(0, 200)}`);
                return text;
            }
            """,
            new { x, y, zoomLevel }).WaitAsync(cancellationToken);
    }
}
