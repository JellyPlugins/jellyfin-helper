using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.Backup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Extended branch coverage for <see cref="BackupController.ImportBackupAsync"/> beyond
///     what <see cref="BackupControllerTests"/> already exercises. Focuses on the malformed-JSON,
///     invalid-structure, and validation-failure branches that were previously uncovered.
///     <para>
///         These tests deliberately push the controller through pathways that a real client
///         would only hit if the client is buggy, hostile, or the file is corrupted mid-upload -
///         situations where a silent partial import would be worst-case for user data integrity.
///     </para>
/// </summary>
public class BackupControllerExtendedTests
{
    private readonly PluginLogService _log = TestMockFactory.CreatePluginLogService();

    // ================================================================================================
    // Malformed JSON - must fail with 400 + informative message; MUST NOT crash the controller.
    // ================================================================================================

    [Fact]
    public async Task ImportBackup_WhenBodyContainsMalformedJson_ReturnsBadRequestWithoutCrashing()
    {
        // BUG GUARD: a hand-edited backup with a stray comma is the most common corruption
        // scenario. In this implementation DeserializeBackup swallows JsonException and returns
        // null, so the surface response is the "Could not parse JSON structure" 400 rather than
        // the outer catch(JsonException) branch. Either way, the controller MUST NOT propagate
        // an unhandled exception (which would surface as an ugly 500 to the frontend).
        _log.Clear();
        var tempDir = CreateTempDir();
        try
        {
            const string malformedJson = "{ \"backupVersion\": 1, ,";
            var controller = CreateControllerWithJsonBody(tempDir, malformedJson);

            var result = await controller.ImportBackupAsync();

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payloadJson = JsonSerializer.Serialize(badRequest.Value);
            Assert.Contains("Invalid backup file", payloadJson, StringComparison.Ordinal);

            // Either surface response is acceptable - both cover the "corrupted JSON" contract.
            // The important guarantee is 400, not 500, and a client-visible "Invalid backup file"
            // prefix so the frontend can display a consistent error banner regardless of which
            // branch the parse failure took.
            var acceptable = payloadJson.Contains("Could not parse JSON structure", StringComparison.Ordinal)
                             || payloadJson.Contains("could not be parsed", StringComparison.Ordinal);
            Assert.True(
                acceptable,
                $"Expected one of the two Invalid-backup-file variants; got: {payloadJson}");
        }
        finally
        {
            _log.Clear();
            Directory.Delete(tempDir, true);
        }
    }

    // ================================================================================================
    // Structurally valid JSON that fails to deserialize into BackupData - must return 400.
    // ================================================================================================

    [Fact]
    public async Task ImportBackup_WhenBodyIsJsonLiteralNull_ReturnsBadRequestWithInvalidStructureMessage()
    {
        // BUG GUARD: DeserializeBackup returns null when the payload deserializes to a null
        // reference (JSON literal "null"). Without the explicit null guard the RestoreBackup
        // call downstream would fail on a NullReferenceException - a 500, not a 400.
        _log.Clear();
        var tempDir = CreateTempDir();
        try
        {
            var controller = CreateControllerWithJsonBody(tempDir, "null");

            var result = await controller.ImportBackupAsync();

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payloadJson = JsonSerializer.Serialize(badRequest.Value);
            Assert.Contains("Invalid backup file", payloadJson, StringComparison.Ordinal);
            Assert.Contains("Could not parse JSON structure", payloadJson, StringComparison.Ordinal);
        }
        finally
        {
            _log.Clear();
            Directory.Delete(tempDir, true);
        }
    }

    // ================================================================================================
    // Actual streamed body exceeds limit even though Content-Length was under the cap.
    // Covers the "totalBytes > MaxBackupSizeBytes" chunk-loop branch that Content-Length checks miss.
    // ================================================================================================

    [Fact]
    public async Task ImportBackup_WhenActualBodyExceedsLimit_ReturnsBadRequestFromChunkLoop()
    {
        // BUG GUARD: a hostile client can lie in Content-Length and send more. Or, more
        // realistically, a chunked-transfer request has no Content-Length at all. The chunk
        // loop's inline size check is the only defence - if it ever regresses to unbounded
        // buffering, memory exhaustion becomes trivially exploitable.
        _log.Clear();
        var tempDir = CreateTempDir();
        try
        {
            // Body exceeds MaxBackupSizeBytes but we omit Content-Length so the pre-check passes.
            var oversized = new string('a', (int)BackupService.MaxBackupSizeBytes + 100);
            // Pass contentLength: 0 so the early Content-Length guard does NOT reject before
            // the chunk loop runs. HttpContext.Request.ContentLength = 0 makes the pre-check
            // a no-op and forces the chunk-loop path to be exercised.
            var controller = CreateControllerWithJsonBody(tempDir, oversized, contentLength: 0);

            var result = await controller.ImportBackupAsync();

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payloadJson = JsonSerializer.Serialize(badRequest.Value);
            Assert.Contains("Maximum size is 10 MB", payloadJson, StringComparison.Ordinal);

            var logs = _log.GetEntries(source: "API", limit: 20);
            Assert.Contains(
                logs,
                entry => entry.Level == "WARN" &&
                         entry.Message.Contains("actual body too large", StringComparison.Ordinal));
        }
        finally
        {
            _log.Clear();
            Directory.Delete(tempDir, true);
        }
    }

    // ================================================================================================
    // Whitespace-only body - must be treated as "empty" (currently is, via IsNullOrWhiteSpace).
    // ================================================================================================

    [Fact]
    public async Task ImportBackup_WhenBodyIsWhitespaceOnly_ReturnsBadRequest()
    {
        // BUG GUARD: an accidental drag-and-drop of a blank file (only a BOM or a newline)
        // must be rejected with the same "No backup data provided" message as a truly-empty
        // body. Any regression to IsNullOrEmpty would let the whitespace pass to DeserializeBackup
        // and surface a confusing JsonException instead.
        _log.Clear();
        var tempDir = CreateTempDir();
        try
        {
            var controller = CreateControllerWithJsonBody(tempDir, "   \n\t  ");

            var result = await controller.ImportBackupAsync();

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payloadJson = JsonSerializer.Serialize(badRequest.Value);
            Assert.Contains("No backup data provided", payloadJson, StringComparison.Ordinal);
        }
        finally
        {
            _log.Clear();
            Directory.Delete(tempDir, true);
        }
    }

    // ================================================================================================
    // Cancellation: request abort during body read must propagate as OperationCanceledException,
    // not be swallowed by the I/O catch block and returned as a 400 "Failed to read body".
    // ================================================================================================

    [Fact]
    public async Task ImportBackup_WhenRequestCancelledDuringBodyRead_ThrowsOperationCanceledException()
    {
        // OperationCanceledException escaped the inner I/O catch
        // (which only caught IOException/ObjectDisposedException/DecoderFallbackException) and
        // then also escaped the outer typed catches, surfacing as an unhandled exception.
        // (log + 499/cancellation response) rather than silently eating the cancellation.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var tempDir = CreateTempDir();
        try
        {
            var controller = CreateControllerWithJsonBody(
                tempDir,
                "{\"backupVersion\":1}",
                requestAborted: cts.Token);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => controller.ImportBackupAsync());
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // ================================================================================================
    // Non-JSON Content-Type - the controller must reject before touching the body.
    // ================================================================================================

    [Fact]
    public async Task ImportBackup_WhenContentTypeIsNotJson_ReturnsBadRequest()
    {
        // A client that POSTs the file with the wrong Content-Type (text/plain) must be told
        // plainly what is expected, rather than having the body streamed and mis-parsed.
        _log.Clear();
        var tempDir = CreateTempDir();
        try
        {
            var controller = CreateController(tempDir);

            // AddJsonBodyToController hardcodes application/json, so build the context by hand
            // to exercise the HasJsonContentType() == false guard.
            var httpContext = new DefaultHttpContext();
            var bodyBytes = Encoding.UTF8.GetBytes("{\"backupVersion\":1}");
            var bodyStream = new MemoryStream(bodyBytes);
            httpContext.Request.Body = bodyStream;
            httpContext.Response.RegisterForDispose(bodyStream);
            httpContext.Request.ContentType = "text/plain";
            httpContext.Request.ContentLength = bodyBytes.Length;
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var result = await controller.ImportBackupAsync();

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payloadJson = JsonSerializer.Serialize(badRequest.Value);
            Assert.Contains("Expected Content-Type: application/json", payloadJson, StringComparison.Ordinal);
        }
        finally
        {
            _log.Clear();
            Directory.Delete(tempDir, true);
        }
    }

    // ================================================================================================
    // Body read fails with a non-cancellation I/O error - must map to a clean 400, not escape as 500.
    // ================================================================================================

    [Fact]
    public async Task ImportBackup_WhenBodyReadThrowsIOException_ReturnsBadRequestFailedToRead()
    {
        // A disk/socket failure mid-read is a real, non-hostile condition. It is NOT a cancellation,
        // so it must be caught by the inner typed catch and surfaced as a client-friendly 400 rather
        // than an opaque 500.
        _log.Clear();
        var tempDir = CreateTempDir();
        try
        {
            var controller = CreateController(tempDir);

            var httpContext = new DefaultHttpContext();
            var throwingStream = new ThrowingBodyStream();
            httpContext.Request.Body = throwingStream;
            httpContext.Response.RegisterForDispose(throwingStream);
            httpContext.Request.ContentType = "application/json";
            // No Content-Length so the early size guard is a no-op and the chunk loop runs the read.
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var result = await controller.ImportBackupAsync();

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payloadJson = JsonSerializer.Serialize(badRequest.Value);
            Assert.Contains("Failed to read the request body.", payloadJson, StringComparison.Ordinal);

            var logs = _log.GetEntries(source: "API", limit: 20);
            Assert.Contains(logs,
                entry => entry.Level == "ERROR" &&
                         entry.Message.Contains("Failed to read backup request body", StringComparison.Ordinal));
        }
        finally
        {
            _log.Clear();
            Directory.Delete(tempDir, true);
        }
    }

    // ================================================================================================
    // Deserializable backup that fails hard validation - must return 400 with the errors surfaced,
    // and log both the per-error detail and the aggregate rejection.
    // ================================================================================================

    [Fact]
    public async Task ImportBackup_WhenBackupFailsValidation_ReturnsBadRequestWithErrorsAndWarnings()
    {
        // An unsupported backupVersion parses fine but is a hard validation error, so IsValid is
        // false and the restore must be refused - a partial import of an unknown-format backup is
        // the worst case for data integrity.
        _log.Clear();
        var tempDir = CreateTempDir();
        try
        {
            var backupJson = JsonSerializer.Serialize(new { backupVersion = 2, useTrash = false });
            var controller = CreateControllerWithJsonBody(tempDir, backupJson);

            var result = await controller.ImportBackupAsync();

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var payloadJson = JsonSerializer.Serialize(badRequest.Value);
            Assert.Contains("Backup validation failed with", payloadJson, StringComparison.Ordinal);
            Assert.Contains("errors", payloadJson, StringComparison.OrdinalIgnoreCase);

            var backupLogs = _log.GetEntries(source: "Backup", limit: 20);
            Assert.Contains(backupLogs,
                entry => entry.Level == "ERROR" &&
                         entry.Message.Contains("Validation error:", StringComparison.Ordinal));

            var apiLogs = _log.GetEntries(source: "API", limit: 20);
            Assert.Contains(apiLogs,
                entry => entry.Level == "WARN" &&
                         entry.Message.Contains("Backup import rejected:", StringComparison.Ordinal));
        }
        finally
        {
            _log.Clear();
            Directory.Delete(tempDir, true);
        }
    }

    // ================================================================================================
    // Reflection glue
    // ================================================================================================

    /// <summary>
    ///     A request-body stream whose read throws <see cref="IOException"/> to simulate a disk or
    ///     socket failure mid-upload. Only <c>ReadAsync</c> needs to fault for the controller's
    ///     chunk loop to hit the inner typed catch.
    /// </summary>
    private sealed class ThrowingBodyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new IOException("Simulated body read failure.");

        public override int Read(byte[] buffer, int offset, int count)
            => throw new IOException("Simulated body read failure.");

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private BackupController CreateController(string dataPath)
        => ControllerTestFactory.CreateBackupController(dataPath: dataPath, pluginLog: _log);

    private BackupController CreateControllerWithJsonBody(
        string dataPath,
        string jsonBody,
        long? contentLength = null,
        CancellationToken requestAborted = default)
        => (BackupController)ControllerTestFactory.AddJsonBodyToController(
            CreateController(dataPath),
            jsonBody,
            contentLength,
            requestAborted);

    private static string CreateTempDir()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "jh-backup-api-ext-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }
}