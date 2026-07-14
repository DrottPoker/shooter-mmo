namespace ShooterMmo
{
    public enum ClientOperation
    {
        None,
        Authenticate,
        Logout,
        LoadSelection,
        CreateCharacter,
        RefreshCharacters,
        RefreshShards,
        JoinShard,
        RefreshSimulationSession,
        LeaveShard
    }

    public sealed class ClientOperationState
    {
        public ClientOperation Current { get; private set; }

        public bool IsBusy
        {
            get { return Current != ClientOperation.None; }
        }

        public bool TryBegin(ClientOperation operation)
        {
            if (operation == ClientOperation.None || IsBusy)
            {
                return false;
            }

            Current = operation;
            return true;
        }

        public void Complete(ClientOperation operation)
        {
            if (Current == operation)
            {
                Current = ClientOperation.None;
            }
        }
    }
}
