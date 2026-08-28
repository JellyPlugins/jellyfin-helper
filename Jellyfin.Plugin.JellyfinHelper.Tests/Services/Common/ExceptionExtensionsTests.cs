using System;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Common;

/// <summary>
///     Tests for IsFatal. Contract: OutOfMemoryException, StackOverflowException and AccessViolationException must return true; all other exception types must return false.
/// </summary>
public sealed class ExceptionExtensionsTests
{
    [Fact]
    public void IsFatal_OutOfMemoryException_ReturnsTrue()
        => Assert.True(new OutOfMemoryException().IsFatal());

    [Fact]
    public void IsFatal_StackOverflowException_ReturnsTrue()
        => Assert.True(new StackOverflowException().IsFatal());

    [Fact]
    public void IsFatal_AccessViolationException_ReturnsTrue()
        => Assert.True(new AccessViolationException().IsFatal());

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(Exception))]
    public void IsFatal_NonFatalExceptions_ReturnsFalse(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;
        Assert.False(ex.IsFatal());
    }
}
