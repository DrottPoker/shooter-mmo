namespace Mmorpg.GameService.Placement.Persistence;

internal static class JoinTicketTransportKeyMigration
{
    public const string Id = "202611171400_join_ticket_transport_key";

    // The 32-byte transport key that a join ticket gives its client and, once a proof of it
    // consumed the ticket, the worker; the consume clears it. A ticket is found by its public id,
    // so the hash of the old opaque ticket is no longer written. Expand only: a later version
    // drops ticket_hash once no running GameService reads it.
    public const string Sql = """
        alter table simulation_join_tickets
            add column transport_key bytea null,
            add constraint ck_simulation_join_tickets_transport_key
                check (transport_key is null or octet_length(transport_key) = 32),
            alter column ticket_hash drop not null;
        """;
}
