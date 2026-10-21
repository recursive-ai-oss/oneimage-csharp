using Xunit;

namespace OneImage.Tests;

public class SkeletonTests
{
    [Fact]
    public void LibraryIsReferenced() => Assert.False(string.IsNullOrEmpty(Library.Name));
}
