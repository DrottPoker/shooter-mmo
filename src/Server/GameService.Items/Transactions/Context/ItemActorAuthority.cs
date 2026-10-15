using System.Security.Cryptography;
using System.Text;
using static Mmorpg.GameService.Items.ItemTransactionRejection;

namespace Mmorpg.GameService.Items.Transactions;

// What the actor of one item transaction may do: system, account, NPC service, and
// live container access.
internal sealed class ItemActorAuthority(ItemTransactionActor actor)
{
    private ItemTransactionActor Actor { get; } = actor;

    // Checks that the actor's authority fields are complete and consistent before any
    // live authority or claim. Kernel only.
    public static void EnsureValid(ItemTransactionActor actor)
    {
        new ItemActorAuthority(actor).EnsureActorIsValid();
    }

    public void EnsureSystemAuthority()
    {
        if (Actor.Authority != ItemTransactionAuthority.System)
        {
            Reject(
                ItemTransactionErrorCodes.AuthorityRequired,
                "This item operation requires system authority.");
        }
    }

    public void EnsureAccountAuthority(Guid accountId)
    {
        if ((Actor.Authority is ItemTransactionAuthority.Account
                or ItemTransactionAuthority.SimulationWorker)
            && Actor.AccountId != accountId)
        {
            Reject(
                ItemTransactionErrorCodes.ItemNotOwned,
                "The account does not own the requested item state.");
        }
    }

    public void EnsureInsuranceNpcAccess()
    {
        if (Actor.Authority != ItemTransactionAuthority.SimulationWorker
            || !Actor.Simulation!.LiveAccess.InsuranceNpc)
        {
            Reject(
                ItemTransactionErrorCodes.AuthorityRequired,
                "This operation requires a validated insurance NPC interaction.");
        }
    }

    public void EnsureQuestNpcAccess()
    {
        if (Actor.Authority != ItemTransactionAuthority.SimulationWorker
            || !Actor.Simulation!.LiveAccess.QuestNpc)
        {
            Reject(
                ItemTransactionErrorCodes.AuthorityRequired,
                "This operation requires a validated quest NPC interaction.");
        }
    }

    // Bank and Recovery Storage are city services. Only a live character at a validated
    // service point may change them, so offline account routes can never reach them.
    public void EnsureLiveContainerAccess(string? containerType)
    {
        if (Actor.Authority == ItemTransactionAuthority.System
            || string.IsNullOrWhiteSpace(containerType))
        {
            return;
        }

        var isLive = Actor.Authority == ItemTransactionAuthority.SimulationWorker;
        if (string.Equals(containerType, "bank", StringComparison.Ordinal)
            && !(isLive && Actor.Simulation!.LiveAccess.Bank))
        {
            Reject(
                ItemTransactionErrorCodes.BankAccessRequired,
                isLive
                    ? "The character is outside validated bank access."
                    : "Bank access requires the character at a city bank in the world.");
        }

        if (string.Equals(containerType, "recovery_storage", StringComparison.Ordinal)
            && !(isLive && Actor.Simulation!.LiveAccess.RecoveryStorage))
        {
            Reject(
                ItemTransactionErrorCodes.RecoveryAccessRequired,
                isLive
                    ? "The character is outside validated Recovery Storage access."
                    : "Recovery Storage access requires the character at a city service in the world.");
        }
    }

    public static bool IsValidIdentifier(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    public static bool HashesMatch(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private void EnsureActorIsValid()
    {
        if (Actor.Authority == ItemTransactionAuthority.Account)
        {
            if (Actor.AccountId is null || Actor.AccountId == Guid.Empty)
            {
                Reject(
                    ItemTransactionErrorCodes.AuthorityRequired,
                    "An account-authorized item operation requires an account id.");
            }

            if (Actor.Simulation is not null)
            {
                Reject(
                    ItemTransactionErrorCodes.AuthorityRequired,
                    "An account-authorized item operation cannot supply simulation authority.");
            }

            return;
        }

        if (Actor.Authority == ItemTransactionAuthority.System)
        {
            if (Actor.AccountId is not null
                || Actor.RequiresOfflineCharacter
                || Actor.Simulation is not null)
            {
                Reject(
                    ItemTransactionErrorCodes.AuthorityRequired,
                    "A system item operation cannot impersonate account or simulation authority.");
            }

            return;
        }

        if (Actor.Authority == ItemTransactionAuthority.SimulationWorker)
        {
            var simulation = Actor.Simulation;
            if (Actor.AccountId is null
                || Actor.AccountId == Guid.Empty
                || Actor.RequiresOfflineCharacter
                || simulation is null
                || simulation.SimulationSessionId == Guid.Empty
                || simulation.CharacterId == Guid.Empty
                || !IsValidIdentifier(simulation.WorkerId)
                || !IsValidIdentifier(simulation.WorkerRuntimeId)
                || !IsValidIdentifier(simulation.ShardId)
                || string.IsNullOrWhiteSpace(simulation.SessionTokenHash)
                || simulation.LiveAccess is null)
            {
                Reject(
                    ItemTransactionErrorCodes.AuthorityRequired,
                    "A simulation-authorized item operation requires complete live authority.");
            }

            return;
        }

        Reject(
            ItemTransactionErrorCodes.AuthorityRequired,
            "The item operation authority is invalid.");
    }
}
