namespace DbChronicle.Migrations.Executor
{
    public sealed class MigrationEnvironmentException : DbChronicleException
    {
        public MigrationEnvironmentException(string message)
            : base(message, 5)
        {
        }
    }
}
