using ZipDriveV3.TestHelpers;

namespace ZipDriveV3.IntegrationTests;

/// <summary>
/// Collection definition for VFS integration tests.
/// Shares a single VfsTestFixture across all test classes in this collection.
/// </summary>
[CollectionDefinition("VfsIntegration")]
public class VfsIntegrationCollection : ICollectionFixture<VfsTestFixture>
{
}
