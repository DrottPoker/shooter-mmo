namespace AuthService.Database.Migrations;

public static class PlayerCorpsePersistenceMigration
{
    public const string Id = "202607171200_player_corpse_persistence";

    public const string Sql = """
        create table corpses (
            id uuid primary key,
            source_type text not null,
            source_character_id uuid null references characters(id) on delete set null,
            source_npc_definition_id text null,
            source_display_name text not null,
            shard_id text not null references shards(id) on delete restrict,
            position_x double precision not null,
            position_y double precision not null,
            position_z double precision not null,
            rotation_x double precision not null,
            rotation_y double precision not null,
            rotation_z double precision not null,
            rotation_w double precision not null,
            persistence_mode text not null,
            presentation_key text not null,
            revision bigint not null default 0,
            created_at timestamptz not null default now(),
            expires_at timestamptz not null,
            closed_at timestamptz null,
            close_reason text null,
            expiry_operation_id uuid not null unique,
            constraint ck_corpses_source_type
                check (source_type in ('player', 'persistent_npc')),
            constraint ck_corpses_source
                check (
                    (
                        source_type = 'player'
                        and source_npc_definition_id is null
                    )
                    or
                    (
                        source_type = 'persistent_npc'
                        and source_character_id is null
                        and length(btrim(source_npc_definition_id)) between 1 and 128
                    )),
            constraint ck_corpses_source_display_name
                check (length(btrim(source_display_name)) between 1 and 128),
            constraint ck_corpses_shard_id
                check (length(btrim(shard_id)) between 1 and 128),
            constraint ck_corpses_position
                check (
                    position_x between -1000000 and 1000000
                    and position_y between -1000000 and 1000000
                    and position_z between -1000000 and 1000000),
            constraint ck_corpses_rotation
                check (
                    rotation_x between -1 and 1
                    and rotation_y between -1 and 1
                    and rotation_z between -1 and 1
                    and rotation_w between -1 and 1
                    and abs(
                        (rotation_x * rotation_x)
                        + (rotation_y * rotation_y)
                        + (rotation_z * rotation_z)
                        + (rotation_w * rotation_w)
                        - 1.0) <= 0.0001),
            constraint ck_corpses_persistence_mode
                check (persistence_mode = 'durable'),
            constraint ck_corpses_presentation_key
                check (length(btrim(presentation_key)) between 1 and 128),
            constraint ck_corpses_revision check (revision >= 0),
            constraint ck_corpses_expiry
                check (
                    expires_at > created_at
                    and (
                        source_type <> 'player'
                        or expires_at = created_at + interval '5 minutes')),
            constraint ck_corpses_lifecycle
                check (
                    (closed_at is null and close_reason is null)
                    or
                    (closed_at is not null
                     and closed_at >= created_at
                     and close_reason in ('expired', 'administrative')))
        );

        create table corpse_sections (
            corpse_id uuid not null references corpses(id) on delete cascade,
            section_kind text not null,
            container_id uuid not null unique references item_containers(id) on delete cascade,
            constraint pk_corpse_sections primary key (corpse_id, section_kind),
            constraint ck_corpse_sections_kind
                check (section_kind in ('general_inventory', 'equipment', 'bag'))
        );

        create table corpse_snapshots (
            id uuid primary key,
            corpse_id uuid not null references corpses(id) on delete cascade,
            snapshot_kind text not null,
            sort_order integer not null,
            definition_id text null references item_definitions(id) on delete restrict,
            equipment_slot_id text null references equipment_slots(id) on delete restrict,
            policy_kind text null,
            presentation_payload jsonb not null,
            created_at timestamptz not null default now(),
            constraint ux_corpse_snapshots_order unique (corpse_id, sort_order),
            constraint ck_corpse_snapshots_kind
                check (snapshot_kind in (
                    'secure_container',
                    'insured_equipment',
                    'protected_bag',
                    'insured_bag')),
            constraint ck_corpse_snapshots_sort_order check (sort_order >= 0),
            constraint ck_corpse_snapshots_policy_kind
                check (policy_kind is null or policy_kind in ('protected_on_death', 'insured')),
            constraint ck_corpse_snapshots_payload
                check (jsonb_typeof(presentation_payload) = 'object'),
            constraint ck_corpse_snapshots_shape
                check (
                    (
                        snapshot_kind = 'secure_container'
                        and definition_id is null
                        and equipment_slot_id is null
                        and policy_kind is null
                    )
                    or
                    (
                        snapshot_kind = 'insured_equipment'
                        and definition_id is not null
                        and equipment_slot_id is not null
                        and equipment_slot_id <> 'bag'
                        and policy_kind = 'insured'
                    )
                    or
                    (
                        snapshot_kind = 'protected_bag'
                        and definition_id is not null
                        and equipment_slot_id = 'bag'
                        and policy_kind = 'protected_on_death'
                    )
                    or
                    (
                        snapshot_kind = 'insured_bag'
                        and definition_id is not null
                        and equipment_slot_id = 'bag'
                        and policy_kind = 'insured'
                    ))
        );

        create table death_events (
            id uuid primary key,
            character_id uuid null references characters(id) on delete set null,
            corpse_id uuid not null unique references corpses(id) on delete restrict,
            operation_id uuid not null unique references item_operations(operation_id) on delete restrict,
            request_hash text not null,
            result_payload jsonb not null,
            committed_at timestamptz not null default now(),
            constraint ck_death_events_request_hash
                check (request_hash ~ '^[0-9a-f]{64}$'),
            constraint ck_death_events_result_payload
                check (jsonb_typeof(result_payload) = 'object')
        );

        create index ix_corpses_open_shard_expiry
            on corpses(shard_id, expires_at, id)
            where closed_at is null;

        create index ix_corpses_due_expiry
            on corpses(expires_at, id)
            where closed_at is null;

        create index ix_corpses_source_character
            on corpses(source_character_id, created_at desc, id)
            where source_character_id is not null;

        create index ix_corpse_sections_corpse
            on corpse_sections(corpse_id, section_kind, container_id);

        create index ix_corpse_snapshots_corpse
            on corpse_snapshots(corpse_id, sort_order, id);
        """;
}
