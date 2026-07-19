namespace AuthService.Database.Migrations;

public static class DevelopmentWorldIdentityMigration
{
    public const string Id = "202607182100_development_world_identity";

    public const string Sql = """
        insert into world_definitions (id, display_name, created_at, updated_at)
        select
            'development-world-1',
            'Development World 1',
            created_at,
            now()
        from world_definitions
        where id = 'local-world-1'
        on conflict (id) do update
        set display_name = excluded.display_name,
            updated_at = now();

        update shards
        set world_id = 'development-world-1',
            updated_at = now()
        where world_id = 'local-world-1';

        delete from world_definitions
        where id = 'local-world-1'
          and not exists (
              select 1
              from shards
              where world_id = 'local-world-1');
        """;
}
