// .NET 10 has no SHA256Cng/SHA512Cng (System.Core, .NET Framework and Mono do). With these two types
// GateCore.cs, PublicManifest.cs and Installer.cs compile unchanged on .NET 10.
#if NET
namespace System.Security.Cryptography
{
    public sealed class SHA256Cng : SHA256
    {
        readonly IncrementalHash _h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public override void Initialize() { byte[] drop = _h.GetHashAndReset(); }
        protected override void HashCore(byte[] b, int o, int n) { _h.AppendData(b, o, n); }
        protected override byte[] HashFinal() { return _h.GetHashAndReset(); }
        protected override void Dispose(bool d) { if (d) _h.Dispose(); base.Dispose(d); }
    }

    public sealed class SHA512Cng : SHA512
    {
        readonly IncrementalHash _h = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        public override void Initialize() { byte[] drop = _h.GetHashAndReset(); }
        protected override void HashCore(byte[] b, int o, int n) { _h.AppendData(b, o, n); }
        protected override byte[] HashFinal() { return _h.GetHashAndReset(); }
        protected override void Dispose(bool d) { if (d) _h.Dispose(); base.Dispose(d); }
    }
}
#endif
