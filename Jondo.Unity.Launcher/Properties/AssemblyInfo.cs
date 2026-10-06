using System.Runtime.CompilerServices;

// So that the tests can look inside at what is not part of the launcher's public
// surface. What matters most here is SecretStore: it is encryption, and encryption without tests is a
// promise. The same workaround Jondo.Unity.Server already had.
[assembly: InternalsVisibleTo("Jondo.Unity.Tests")]
