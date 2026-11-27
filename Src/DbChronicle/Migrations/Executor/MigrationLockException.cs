namespace DbChronicle.Migrations.Executor
{
    public sealed class MigrationLockException : DbChronicleException
    {
        public MigrationLockException(string message)
            : base(message, 4)
        {
        }
    }
}
