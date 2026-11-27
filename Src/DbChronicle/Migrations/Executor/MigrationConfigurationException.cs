namespace DbChronicle.Migrations.Executor
{
    public sealed class MigrationConfigurationException : DbChronicleException
    {
        public MigrationConfigurationException(string message)
            : base(message, 1)
        {
        }
    }
}
