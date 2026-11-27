namespace DbChronicle.Migrations.Executor
{
    public sealed class MigrationStateException : DbChronicleException
    {
        public MigrationStateException(string message)
            : base(message, 3)
        {
        }
    }
}
