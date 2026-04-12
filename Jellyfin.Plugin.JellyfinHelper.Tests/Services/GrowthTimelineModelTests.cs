using System;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services;

public class GrowthTimelineModelTests
{
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ── GrowthTimelinePoint ──────────────────────────────────────────────

    [Fact]
    public void GrowthTimelinePoint_DefaultValues()
    {
        var point = new GrowthTimelinePoint();
        Assert.Equal(default, point.Date);
        Assert.Equal(0L, point.CumulativeSize);
        Assert.Equal(0, point.CumulativeFileCount);
    }

    [Fact]
    public void GrowthTimelinePoint_SetProperties()
    {
        var date = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var point = new GrowthTimelinePoint
        {
            Date = date,
            CumulativeSize = 1024L * 1024 * 1024 * 50, // 50 GB
            CumulativeFileCount = 1500,
        };

        Assert.Equal(date, point.Date);
        Assert.Equal(1024L * 1024 * 1024 * 50, point.CumulativeSize);
        Assert.Equal(1500, point.CumulativeFileCount);
    }

    [Fact]
    public void GrowthTimelinePoint_SerializesToCamelCase()
    {
        var point = new GrowthTimelinePoint
        {
            Date = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CumulativeSize = 12345,
            CumulativeFileCount = 42,
        };

        var json = JsonSerializer.Serialize(point, CamelCase);
        Assert.Contains("\"date\":", json);
        Assert.Contains("\"cumulativeSize\":12345", json);
        Assert.Contains("\"cumulativeFileCount\":42", json);
    }

    [Fact]
    public void GrowthTimelinePoint_DeserializesFromCamelCase()
    {
        var json = """{"date":"2025-01-01T00:00:00Z","cumulativeSize":99999,"cumulativeFileCount":7}""";
        var point = JsonSerializer.Deserialize<GrowthTimelinePoint>(json, CamelCase);

        Assert.NotNull(point);
        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), point!.Date);
        Assert.Equal(99999L, point.CumulativeSize);
        Assert.Equal(7, point.CumulativeFileCount);
    }

    // ── GrowthTimelineResult ─────────────────────────────────────────────

    [Fact]
    public void GrowthTimelineResult_DefaultGranularity_IsMonthly()
    {
        var result = new GrowthTimelineResult();
        Assert.Equal("monthly", result.Granularity);
    }

    [Fact]
    public void GrowthTimelineResult_DataPoints_IsInitializedEmpty()
    {
        var result = new GrowthTimelineResult();
        Assert.NotNull(result.DataPoints);
        Assert.Empty(result.DataPoints);
    }

    [Fact]
    public void GrowthTimelineResult_CanAddDataPoints()
    {
        var result = new GrowthTimelineResult();
        result.DataPoints.Add(new GrowthTimelinePoint
        {
            Date = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CumulativeSize = 100,
            CumulativeFileCount = 5,
        });
        result.DataPoints.Add(new GrowthTimelinePoint
        {
            Date = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            CumulativeSize = 200,
            CumulativeFileCount = 10,
        });

        Assert.Equal(2, result.DataPoints.Count);
        Assert.Equal(200L, result.DataPoints[1].CumulativeSize);
    }

    [Fact]
    public void GrowthTimelineResult_SetAllProperties()
    {
        var computedAt = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var earliest = new DateTime(2020, 3, 15, 0, 0, 0, DateTimeKind.Utc);

        var result = new GrowthTimelineResult
        {
            Granularity = "quarterly",
            EarliestFileDate = earliest,
            ComputedAt = computedAt,
            TotalFilesScanned = 5000,
        };

        Assert.Equal("quarterly", result.Granularity);
        Assert.Equal(earliest, result.EarliestFileDate);
        Assert.Equal(computedAt, result.ComputedAt);
        Assert.Equal(5000, result.TotalFilesScanned);
    }

    [Fact]
    public void GrowthTimelineResult_RoundTrip_ScalarPropertiesPreserved()
    {
        var result = new GrowthTimelineResult
        {
            Granularity = "weekly",
            EarliestFileDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ComputedAt = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            TotalFilesScanned = 300,
        };

        var json = JsonSerializer.Serialize(result, CamelCase);
        var deserialized = JsonSerializer.Deserialize<GrowthTimelineResult>(json, CamelCase);

        Assert.NotNull(deserialized);
        Assert.Equal("weekly", deserialized!.Granularity);
        Assert.Equal(result.EarliestFileDate, deserialized.EarliestFileDate);
        Assert.Equal(result.ComputedAt, deserialized.ComputedAt);
        Assert.Equal(300, deserialized.TotalFilesScanned);
    }

    [Fact]
    public void GrowthTimelineResult_RoundTrip_DataPointsPreserved()
    {
        var result = new GrowthTimelineResult
        {
            Granularity = "monthly",
            EarliestFileDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ComputedAt = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            TotalFilesScanned = 50,
        };
        result.DataPoints.Add(new GrowthTimelinePoint
        {
            Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CumulativeSize = 1000,
            CumulativeFileCount = 10,
        });
        result.DataPoints.Add(new GrowthTimelinePoint
        {
            Date = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            CumulativeSize = 2500,
            CumulativeFileCount = 25,
        });

        var json = JsonSerializer.Serialize(result, CamelCase);
        var deserialized = JsonSerializer.Deserialize<GrowthTimelineResult>(json, CamelCase);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized!.DataPoints.Count);
        Assert.Equal(1000L, deserialized.DataPoints[0].CumulativeSize);
        Assert.Equal(10, deserialized.DataPoints[0].CumulativeFileCount);
        Assert.Equal(2500L, deserialized.DataPoints[1].CumulativeSize);
        Assert.Equal(25, deserialized.DataPoints[1].CumulativeFileCount);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), deserialized.DataPoints[0].Date);
        Assert.Equal(new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), deserialized.DataPoints[1].Date);
    }

    [Fact]
    public void GrowthTimelineResult_Serialization_IncludesDataPointsArray()
    {
        var result = new GrowthTimelineResult();
        result.DataPoints.Add(new GrowthTimelinePoint
        {
            Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CumulativeSize = 1000,
            CumulativeFileCount = 10,
        });
        result.DataPoints.Add(new GrowthTimelinePoint
        {
            Date = new DateTime(2024, 1, 8, 0, 0, 0, DateTimeKind.Utc),
            CumulativeSize = 2000,
            CumulativeFileCount = 20,
        });

        var json = JsonSerializer.Serialize(result, CamelCase);

        // Verify the JSON contains dataPoints with the correct values
        Assert.Contains("\"dataPoints\":[", json);
        Assert.Contains("\"cumulativeSize\":1000", json);
        Assert.Contains("\"cumulativeSize\":2000", json);
        Assert.Contains("\"cumulativeFileCount\":10", json);
        Assert.Contains("\"cumulativeFileCount\":20", json);
    }

    [Fact]
    public void GrowthTimelineResult_SerializesToCamelCase()
    {
        var result = new GrowthTimelineResult
        {
            Granularity = "yearly",
            TotalFilesScanned = 42,
        };

        var json = JsonSerializer.Serialize(result, CamelCase);

        Assert.Contains("\"granularity\":\"yearly\"", json);
        Assert.Contains("\"totalFilesScanned\":42", json);
        Assert.Contains("\"earliestFileDate\":", json);
        Assert.Contains("\"computedAt\":", json);
        Assert.Contains("\"dataPoints\":[]", json);
    }

    // ── JSON property naming (ensures [JsonPropertyName] attributes work) ──

    [Fact]
    public void GrowthTimelinePoint_SerializesToCamelCase_WithoutNamingPolicy()
    {
        // This simulates what happens when ASP.NET Core / Jellyfin serializes with default (PascalCase) options.
        // The [JsonPropertyName] attributes must force camelCase regardless of the global naming policy.
        var point = new GrowthTimelinePoint
        {
            Date = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CumulativeSize = 12345,
            CumulativeFileCount = 42,
        };

        var json = JsonSerializer.Serialize(point); // no options → default PascalCase
        Assert.Contains("\"date\":", json);
        Assert.Contains("\"cumulativeSize\":12345", json);
        Assert.Contains("\"cumulativeFileCount\":42", json);
        // Must NOT contain PascalCase variants
        Assert.DoesNotContain("\"Date\":", json);
        Assert.DoesNotContain("\"CumulativeSize\":", json);
        Assert.DoesNotContain("\"CumulativeFileCount\":", json);
    }

    [Fact]
    public void GrowthTimelineResult_SerializesToCamelCase_WithoutNamingPolicy()
    {
        // Simulates ASP.NET Core / Jellyfin controller serialization (potentially PascalCase default).
        var result = new GrowthTimelineResult
        {
            Granularity = "yearly",
            TotalFilesScanned = 42,
            EarliestFileDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ComputedAt = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        result.DataPoints.Add(new GrowthTimelinePoint
        {
            Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CumulativeSize = 5000,
            CumulativeFileCount = 10,
        });

        var json = JsonSerializer.Serialize(result); // no options → default PascalCase
        Assert.Contains("\"granularity\":\"yearly\"", json);
        Assert.Contains("\"totalFilesScanned\":42", json);
        Assert.Contains("\"earliestFileDate\":", json);
        Assert.Contains("\"computedAt\":", json);
        Assert.Contains("\"dataPoints\":[", json);
        Assert.Contains("\"cumulativeSize\":5000", json);
        // Must NOT contain PascalCase variants
        Assert.DoesNotContain("\"Granularity\":", json);
        Assert.DoesNotContain("\"DataPoints\":", json);
        Assert.DoesNotContain("\"TotalFilesScanned\":", json);
    }

    [Fact]
    public void GrowthTimelineResult_DeserializesFromCamelCase_WithoutNamingPolicy()
    {
        // Verifies that [JsonPropertyName] allows deserialization without a naming policy
        var json = """
        {
            "granularity": "monthly",
            "earliestFileDate": "2024-01-01T00:00:00Z",
            "computedAt": "2025-06-01T00:00:00Z",
            "totalFilesScanned": 100,
            "dataPoints": [
                {"date": "2024-01-01T00:00:00Z", "cumulativeSize": 1000, "cumulativeFileCount": 10},
                {"date": "2024-02-01T00:00:00Z", "cumulativeSize": 2000, "cumulativeFileCount": 20}
            ]
        }
        """;

        var result = JsonSerializer.Deserialize<GrowthTimelineResult>(json); // no options
        Assert.NotNull(result);
        Assert.Equal("monthly", result!.Granularity);
        Assert.Equal(100, result.TotalFilesScanned);
        Assert.Equal(2, result.DataPoints.Count);
        Assert.Equal(1000L, result.DataPoints[0].CumulativeSize);
        Assert.Equal(2000L, result.DataPoints[1].CumulativeSize);
    }

    [Fact]
    public void GrowthTimelineResult_FirstScanTimestamp_DefaultsToNull()
    {
        var result = new GrowthTimelineResult();
        Assert.Null(result.FirstScanTimestamp);
    }

    [Fact]
    public void GrowthTimelineResult_FirstScanTimestamp_CanBeSet()
    {
        var timestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = new GrowthTimelineResult { FirstScanTimestamp = timestamp };
        Assert.Equal(timestamp, result.FirstScanTimestamp);
    }

    [Fact]
    public void GrowthTimelineResult_FirstScanTimestamp_RoundTrip()
    {
        var timestamp = new DateTime(2025, 3, 15, 12, 30, 0, DateTimeKind.Utc);
        var result = new GrowthTimelineResult
        {
            Granularity = "monthly",
            FirstScanTimestamp = timestamp,
        };

        var json = JsonSerializer.Serialize(result, CamelCase);
        Assert.Contains("\"firstScanTimestamp\":", json);

        var deserialized = JsonSerializer.Deserialize<GrowthTimelineResult>(json, CamelCase);
        Assert.NotNull(deserialized);
        Assert.Equal(timestamp, deserialized!.FirstScanTimestamp);
    }

    [Fact]
    public void GrowthTimelineResult_FirstScanTimestamp_NullRoundTrip()
    {
        var result = new GrowthTimelineResult { Granularity = "monthly" };

        var json = JsonSerializer.Serialize(result, CamelCase);
        var deserialized = JsonSerializer.Deserialize<GrowthTimelineResult>(json, CamelCase);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized!.FirstScanTimestamp);
    }

    // ── GrowthTimelineBaseline ────────────────────────────────────────────

    [Fact]
    public void GrowthTimelineBaseline_DefaultValues()
    {
        var baseline = new GrowthTimelineBaseline();
        Assert.Equal(default, baseline.FirstScanTimestamp);
        Assert.NotNull(baseline.Directories);
        Assert.Empty(baseline.Directories);
    }

    [Fact]
    public void GrowthTimelineBaseline_CanAddDirectories()
    {
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/movies/MovieA"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
        };

        Assert.Single(baseline.Directories);
        Assert.Equal(8_000_000_000L, baseline.Directories["/movies/MovieA"].Size);
    }

    [Fact]
    public void GrowthTimelineBaseline_RoundTrip()
    {
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/movies/MovieA"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
        };
        baseline.Directories["/movies/MovieB"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 9, 15, 0, 0, 0, DateTimeKind.Utc),
            Size = 5_000_000_000,
        };

        var json = JsonSerializer.Serialize(baseline, CamelCase);
        var deserialized = JsonSerializer.Deserialize<GrowthTimelineBaseline>(json, CamelCase);

        Assert.NotNull(deserialized);
        Assert.Equal(baseline.FirstScanTimestamp, deserialized!.FirstScanTimestamp);
        Assert.Equal(2, deserialized.Directories.Count);
        Assert.Equal(8_000_000_000L, deserialized.Directories["/movies/MovieA"].Size);
        Assert.Equal(new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), deserialized.Directories["/movies/MovieA"].CreatedUtc);
    }

    // ── BaselineDirectoryEntry ────────────────────────────────────────────

    [Fact]
    public void BaselineDirectoryEntry_DefaultValues()
    {
        var entry = new BaselineDirectoryEntry();
        Assert.Equal(default, entry.CreatedUtc);
        Assert.Equal(0L, entry.Size);
    }

    [Fact]
    public void BaselineDirectoryEntry_SetProperties()
    {
        var entry = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 10_000_000_000,
        };

        Assert.Equal(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), entry.CreatedUtc);
        Assert.Equal(10_000_000_000L, entry.Size);
    }

    [Fact]
    public void BaselineDirectoryEntry_RoundTrip()
    {
        var entry = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 10_000_000_000,
        };

        var json = JsonSerializer.Serialize(entry, CamelCase);
        Assert.Contains("\"createdUtc\":", json);
        Assert.Contains("\"size\":10000000000", json);

        var deserialized = JsonSerializer.Deserialize<BaselineDirectoryEntry>(json, CamelCase);
        Assert.NotNull(deserialized);
        Assert.Equal(entry.CreatedUtc, deserialized!.CreatedUtc);
        Assert.Equal(entry.Size, deserialized.Size);
    }

    [Fact]
    public void GrowthTimelineResult_DataPointsCumulativeSizeGrows()
    {
        var result = new GrowthTimelineResult();

        long runningSize = 0;
        for (int i = 0; i < 12; i++)
        {
            runningSize += 1024L * 1024 * 100; // +100MB per month
            result.DataPoints.Add(new GrowthTimelinePoint
            {
                Date = new DateTime(2025, i + 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CumulativeSize = runningSize,
                CumulativeFileCount = (i + 1) * 10,
            });
        }

        Assert.Equal(12, result.DataPoints.Count);

        // Verify cumulative nature: each point >= previous
        for (int i = 1; i < result.DataPoints.Count; i++)
        {
            Assert.True(
                result.DataPoints[i].CumulativeSize >= result.DataPoints[i - 1].CumulativeSize,
                $"DataPoint {i} has smaller CumulativeSize than {i - 1}");
            Assert.True(
                result.DataPoints[i].CumulativeFileCount >= result.DataPoints[i - 1].CumulativeFileCount,
                $"DataPoint {i} has smaller CumulativeFileCount than {i - 1}");
        }
    }
}