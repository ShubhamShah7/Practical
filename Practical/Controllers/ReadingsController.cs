using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Practical.Models;
using Practical.Services;

namespace Practical.Controllers;

[ApiController]
[Route("readings")]
public class ReadingsController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // POST /readings
    [HttpPost]
    public async Task<IActionResult> Ingest([FromServices] DeviceStore store, CancellationToken ct)
    {
        var count = 0;

        // DeserializeAsyncEnumerable streams the JSON array element-by-element,
        // avoiding a full-body buffer allocation for large batches.
        await foreach (var reading in JsonSerializer.DeserializeAsyncEnumerable<DeviceReading>(
            Request.Body, JsonOptions, ct))
        {
            if (reading.DeviceId is null) continue;

            var epochSecond = new DateTimeOffset(reading.Timestamp, TimeSpan.Zero).ToUnixTimeSeconds();
            store.Ingest(reading.DeviceId, epochSecond, reading.Value);
            count++;
        }

        return Ok(new { accepted = count });
    }

    // GET /readings/{deviceId}
    [HttpGet("{deviceId}")]
    public IActionResult Query(string deviceId, [FromServices] DeviceStore store)
    {
        var result = store.Query(deviceId);
        return result is null ? NotFound(new { error = $"No readings in window for device '{deviceId}'." }) : Ok(result);
    }
}
