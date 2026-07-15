namespace AuthService.Database.Migrations;

internal static class ItemPersistenceFoundationMigration
{
    public const string Id = "202607151200_item_persistence_foundation";

    public const string Sql = """
        create table item_catalog_revisions (
            catalog_id text not null,
            revision text not null,
            format_version integer not null,
            base_secure_container_tier_id text not null,
            is_current boolean not null default false,
            applied_at timestamptz not null default now(),
            constraint pk_item_catalog_revisions
                primary key (catalog_id, revision),
            constraint ck_item_catalog_revisions_format_version
                check (format_version > 0),
            constraint ck_item_catalog_revisions_revision
                check (revision ~ '^[0-9a-f]{64}$')
        );

        create unique index ux_item_catalog_revisions_current_catalog
            on item_catalog_revisions(catalog_id)
            where is_current;

        create table item_categories (
            id text primary key,
            catalog_id text not null,
            catalog_revision text not null,
            display_name text not null,
            is_active boolean not null default true,
            updated_at timestamptz not null default now(),
            constraint fk_item_categories_catalog_revision
                foreign key (catalog_id, catalog_revision)
                references item_catalog_revisions(catalog_id, revision)
                on delete restrict,
            constraint ck_item_categories_id
                check (id ~ '^[a-z][a-z0-9._-]*[a-z0-9]$' or id ~ '^[a-z]$'),
            constraint ck_item_categories_display_name
                check (length(btrim(display_name)) between 1 and 128)
        );

        create table item_tags (
            id text primary key,
            catalog_id text not null,
            catalog_revision text not null,
            display_name text not null,
            is_active boolean not null default true,
            updated_at timestamptz not null default now(),
            constraint fk_item_tags_catalog_revision
                foreign key (catalog_id, catalog_revision)
                references item_catalog_revisions(catalog_id, revision)
                on delete restrict,
            constraint ck_item_tags_id
                check (id ~ '^[a-z][a-z0-9._-]*[a-z0-9]$' or id ~ '^[a-z]$'),
            constraint ck_item_tags_display_name
                check (length(btrim(display_name)) between 1 and 128)
        );

        create table equipment_slots (
            id text primary key,
            display_name text not null,
            sort_order integer not null unique,
            catalog_id text null,
            catalog_revision text null,
            updated_at timestamptz not null default now(),
            constraint fk_equipment_slots_catalog_revision
                foreign key (catalog_id, catalog_revision)
                references item_catalog_revisions(catalog_id, revision)
                on delete restrict,
            constraint ck_equipment_slots_id
                check (id ~ '^[a-z][a-z0-9._-]*[a-z0-9]$' or id ~ '^[a-z]$'),
            constraint ck_equipment_slots_display_name
                check (length(btrim(display_name)) between 1 and 128),
            constraint ck_equipment_slots_sort_order
                check (sort_order >= 0),
            constraint ck_equipment_slots_catalog_revision
                check (
                    (catalog_id is null and catalog_revision is null)
                    or
                    (catalog_id is not null and catalog_revision is not null))
        );

        insert into equipment_slots (id, display_name, sort_order)
        values
            ('head', 'Head', 0),
            ('body_armor', 'Body Armor', 1),
            ('primary_weapon', 'Primary Weapon', 2),
            ('secondary_weapon', 'Secondary Weapon', 3),
            ('tool', 'Tool', 4),
            ('ring_1', 'Ring 1', 5),
            ('ring_2', 'Ring 2', 6),
            ('bag', 'Bag', 7);

        create table item_definitions (
            id text primary key,
            catalog_id text not null,
            catalog_revision text not null,
            display_name text not null,
            category_id text not null references item_categories(id) on delete restrict,
            unit_weight bigint not null,
            maximum_stack_size integer not null,
            player_destroyable boolean not null,
            structural_fingerprint text not null,
            is_active boolean not null default true,
            updated_at timestamptz not null default now(),
            constraint fk_item_definitions_catalog_revision
                foreign key (catalog_id, catalog_revision)
                references item_catalog_revisions(catalog_id, revision)
                on delete restrict,
            constraint ck_item_definitions_id
                check (id ~ '^[a-z][a-z0-9._-]*[a-z0-9]$' or id ~ '^[a-z]$'),
            constraint ck_item_definitions_display_name
                check (length(btrim(display_name)) between 1 and 128),
            constraint ck_item_definitions_unit_weight
                check (unit_weight >= 0),
            constraint ck_item_definitions_maximum_stack_size
                check (maximum_stack_size > 0),
            constraint ck_item_definitions_structural_fingerprint
                check (structural_fingerprint ~ '^[0-9a-f]{64}$')
        );

        create table item_definition_tags (
            definition_id text not null references item_definitions(id) on delete cascade,
            tag_id text not null references item_tags(id) on delete restrict,
            constraint pk_item_definition_tags primary key (definition_id, tag_id)
        );

        create table item_definition_equipment_slots (
            definition_id text not null references item_definitions(id) on delete cascade,
            equipment_slot_id text not null references equipment_slots(id) on delete restrict,
            constraint pk_item_definition_equipment_slots
                primary key (definition_id, equipment_slot_id)
        );

        create table item_definition_location_rules (
            definition_id text not null references item_definitions(id) on delete cascade,
            location_kind text not null,
            is_allowed boolean not null,
            constraint pk_item_definition_location_rules
                primary key (definition_id, location_kind),
            constraint ck_item_definition_location_rules_kind
                check (location_kind in ('secure_container'))
        );

        create table item_definition_default_policies (
            definition_id text not null references item_definitions(id) on delete cascade,
            policy_kind text not null,
            constraint pk_item_definition_default_policies
                primary key (definition_id, policy_kind),
            constraint ck_item_definition_default_policies_kind
                check (policy_kind in ('protected_on_death', 'insured'))
        );

        create table bag_definitions (
            definition_id text primary key references item_definitions(id) on delete cascade,
            carry_capacity_bonus bigint not null,
            constraint ck_bag_definitions_carry_capacity_bonus
                check (carry_capacity_bonus >= 0)
        );

        create table bag_definition_slots (
            definition_id text not null references bag_definitions(definition_id) on delete cascade,
            slot_index integer not null,
            slot_kind text not null,
            constraint pk_bag_definition_slots primary key (definition_id, slot_index),
            constraint ck_bag_definition_slots_index check (slot_index >= 0),
            constraint ck_bag_definition_slots_kind
                check (slot_kind in ('general', 'specialized'))
        );

        create table bag_definition_slot_tags (
            definition_id text not null,
            slot_index integer not null,
            tag_id text not null references item_tags(id) on delete restrict,
            constraint pk_bag_definition_slot_tags
                primary key (definition_id, slot_index, tag_id),
            constraint fk_bag_definition_slot_tags_slot
                foreign key (definition_id, slot_index)
                references bag_definition_slots(definition_id, slot_index)
                on delete cascade
        );

        create table secure_container_tiers (
            id text primary key,
            catalog_id text not null,
            catalog_revision text not null,
            display_name text not null,
            slot_capacity integer not null,
            structural_fingerprint text not null,
            is_active boolean not null default true,
            updated_at timestamptz not null default now(),
            constraint fk_secure_container_tiers_catalog_revision
                foreign key (catalog_id, catalog_revision)
                references item_catalog_revisions(catalog_id, revision)
                on delete restrict,
            constraint ck_secure_container_tiers_id
                check (id ~ '^[a-z][a-z0-9._-]*[a-z0-9]$' or id ~ '^[a-z]$'),
            constraint ck_secure_container_tiers_display_name
                check (length(btrim(display_name)) between 1 and 128),
            constraint ck_secure_container_tiers_slot_capacity
                check (slot_capacity between 1 and 65535),
            constraint ck_secure_container_tiers_structural_fingerprint
                check (structural_fingerprint ~ '^[0-9a-f]{64}$')
        );

        create table item_system_settings (
            id text primary key,
            catalog_id text not null,
            base_carry_capacity bigint not null,
            permanent_inventory_slot_count integer not null,
            bank_slot_count integer not null,
            constraint ck_item_system_settings_base_carry_capacity
                check (base_carry_capacity > 0),
            constraint ck_item_system_settings_inventory_slots
                check (permanent_inventory_slot_count between 1 and 65535),
            constraint ck_item_system_settings_bank_slots
                check (bank_slot_count between 1 and 65535)
        );

        insert into item_system_settings (
            id,
            catalog_id,
            base_carry_capacity,
            permanent_inventory_slot_count,
            bank_slot_count)
        values ('character_default', 'core', 200, 20, 40);

        create table account_secure_container_entitlements (
            account_id uuid primary key references accounts(id) on delete cascade,
            tier_id text not null references secure_container_tiers(id) on delete restrict,
            revision bigint not null default 0,
            created_at timestamptz not null default now(),
            updated_at timestamptz not null default now(),
            constraint ck_account_secure_container_entitlements_revision
                check (revision >= 0)
        );

        create table item_containers (
            id uuid primary key,
            container_type text not null,
            owner_character_id uuid null references characters(id) on delete cascade,
            bound_bag_item_instance_id uuid null,
            slot_capacity integer null,
            revision bigint not null default 0,
            lifecycle text not null default 'active',
            created_at timestamptz not null default now(),
            updated_at timestamptz not null default now(),
            constraint ck_item_containers_type
                check (container_type in (
                    'permanent_inventory',
                    'bank',
                    'secure_container',
                    'recovery_storage',
                    'bag_contents',
                    'corpse_inventory',
                    'corpse_equipment',
                    'corpse_bag_contents')),
            constraint ck_item_containers_revision check (revision >= 0),
            constraint ck_item_containers_lifecycle
                check (lifecycle in ('active', 'closed')),
            constraint ck_item_containers_capacity
                check (
                    (container_type = 'recovery_storage' and slot_capacity is null)
                    or
                    (container_type <> 'recovery_storage'
                     and slot_capacity between 1 and 65535)),
            constraint ck_item_containers_binding
                check (
                    (
                        container_type in (
                            'permanent_inventory',
                            'bank',
                            'secure_container',
                            'recovery_storage')
                        and owner_character_id is not null
                        and bound_bag_item_instance_id is null
                    )
                    or
                    (
                        container_type = 'bag_contents'
                        and owner_character_id is null
                        and bound_bag_item_instance_id is not null
                    )
                    or
                    (
                        container_type in (
                            'corpse_inventory',
                            'corpse_equipment',
                            'corpse_bag_contents')
                        and owner_character_id is null
                        and bound_bag_item_instance_id is null
                    ))
        );

        create unique index ux_item_containers_active_character_type
            on item_containers(owner_character_id, container_type)
            where lifecycle = 'active'
              and owner_character_id is not null
              and container_type in (
                  'permanent_inventory',
                  'bank',
                  'secure_container',
                  'recovery_storage');

        create unique index ux_item_containers_bound_bag
            on item_containers(bound_bag_item_instance_id)
            where bound_bag_item_instance_id is not null;

        create table item_container_slots (
            container_id uuid not null references item_containers(id) on delete cascade,
            slot_index integer not null,
            slot_kind text not null,
            constraint pk_item_container_slots primary key (container_id, slot_index),
            constraint ck_item_container_slots_index check (slot_index >= 0),
            constraint ck_item_container_slots_kind
                check (slot_kind in ('general', 'specialized'))
        );

        create table item_container_slot_tags (
            container_id uuid not null,
            slot_index integer not null,
            tag_id text not null references item_tags(id) on delete restrict,
            constraint pk_item_container_slot_tags
                primary key (container_id, slot_index, tag_id),
            constraint fk_item_container_slot_tags_slot
                foreign key (container_id, slot_index)
                references item_container_slots(container_id, slot_index)
                on delete cascade
        );

        create table character_item_states (
            character_id uuid primary key references characters(id) on delete cascade,
            revision bigint not null default 0,
            carried_weight bigint not null default 0,
            base_carry_capacity bigint not null,
            carry_capacity bigint not null,
            permanent_inventory_container_id uuid not null unique,
            bank_container_id uuid not null unique,
            secure_container_id uuid not null unique,
            recovery_storage_container_id uuid not null unique,
            created_at timestamptz not null default now(),
            updated_at timestamptz not null default now(),
            constraint fk_character_item_states_inventory_container
                foreign key (permanent_inventory_container_id)
                references item_containers(id)
                on delete no action
                deferrable initially deferred,
            constraint fk_character_item_states_bank_container
                foreign key (bank_container_id)
                references item_containers(id)
                on delete no action
                deferrable initially deferred,
            constraint fk_character_item_states_secure_container
                foreign key (secure_container_id)
                references item_containers(id)
                on delete no action
                deferrable initially deferred,
            constraint fk_character_item_states_recovery_container
                foreign key (recovery_storage_container_id)
                references item_containers(id)
                on delete no action
                deferrable initially deferred,
            constraint ck_character_item_states_revision check (revision >= 0),
            constraint ck_character_item_states_weight check (carried_weight >= 0),
            constraint ck_character_item_states_capacity
                check (base_carry_capacity > 0 and carry_capacity >= base_carry_capacity),
            constraint ck_character_item_states_hard_cap
                check (
                    carried_weight::numeric * 100
                    <= carry_capacity::numeric * 140),
            constraint ck_character_item_states_distinct_containers
                check (
                    permanent_inventory_container_id <> bank_container_id
                    and permanent_inventory_container_id <> secure_container_id
                    and permanent_inventory_container_id <> recovery_storage_container_id
                    and bank_container_id <> secure_container_id
                    and bank_container_id <> recovery_storage_container_id
                    and secure_container_id <> recovery_storage_container_id)
        );

        create table item_instances (
            id uuid primary key,
            definition_id text not null references item_definitions(id) on delete restrict,
            quantity integer not null,
            revision bigint not null default 0,
            container_id uuid null references item_containers(id) on delete cascade,
            container_slot_index integer null,
            equipped_character_id uuid null references characters(id) on delete cascade,
            equipment_slot_id text null references equipment_slots(id) on delete restrict,
            created_at timestamptz not null default now(),
            updated_at timestamptz not null default now(),
            constraint fk_item_instances_container_slot
                foreign key (container_id, container_slot_index)
                references item_container_slots(container_id, slot_index)
                on delete no action
                deferrable initially deferred,
            constraint ux_item_instances_container_slot
                unique (container_id, container_slot_index)
                deferrable initially immediate,
            constraint ux_item_instances_equipment_slot
                unique (equipped_character_id, equipment_slot_id)
                deferrable initially immediate,
            constraint ck_item_instances_quantity check (quantity > 0),
            constraint ck_item_instances_revision check (revision >= 0),
            constraint ck_item_instances_location_union
                check (
                    (
                        container_id is not null
                        and container_slot_index is not null
                        and equipped_character_id is null
                        and equipment_slot_id is null
                    )
                    or
                    (
                        container_id is null
                        and container_slot_index is null
                        and equipped_character_id is not null
                        and equipment_slot_id is not null
                    ))
        );

        alter table item_containers
            add constraint fk_item_containers_bound_bag
            foreign key (bound_bag_item_instance_id)
            references item_instances(id)
            on delete cascade
            deferrable initially deferred;

        create table item_instance_policies (
            id uuid primary key,
            item_instance_id uuid not null references item_instances(id) on delete cascade,
            policy_kind text not null,
            status text not null default 'active',
            source_kind text not null,
            source_id text not null,
            revision bigint not null default 0,
            created_at timestamptz not null default now(),
            consumed_at timestamptz null,
            removed_at timestamptz null,
            constraint ck_item_instance_policies_kind
                check (policy_kind in ('protected_on_death', 'insured')),
            constraint ck_item_instance_policies_status
                check (status in ('active', 'consumed', 'removed')),
            constraint ck_item_instance_policies_source
                check (length(btrim(source_kind)) > 0 and length(btrim(source_id)) > 0),
            constraint ck_item_instance_policies_revision check (revision >= 0),
            constraint ck_item_instance_policies_lifecycle
                check (
                    (status = 'active' and consumed_at is null and removed_at is null)
                    or
                    (status = 'consumed' and consumed_at is not null and removed_at is null)
                    or
                    (status = 'removed' and consumed_at is null and removed_at is not null))
        );

        create unique index ux_item_instance_policies_active_kind
            on item_instance_policies(item_instance_id, policy_kind)
            where status = 'active';

        create table recovery_deliveries (
            id uuid primary key,
            character_id uuid not null references characters(id) on delete cascade,
            recovery_storage_container_id uuid not null references item_containers(id) on delete cascade,
            source_kind text not null,
            source_event_id text not null,
            revision bigint not null default 0,
            created_at timestamptz not null default now(),
            available_at timestamptz null,
            expires_at timestamptz null,
            claimed_at timestamptz null,
            constraint ux_recovery_deliveries_source
                unique (character_id, source_kind, source_event_id),
            constraint ck_recovery_deliveries_source
                check (length(btrim(source_kind)) > 0 and length(btrim(source_event_id)) > 0),
            constraint ck_recovery_deliveries_revision check (revision >= 0),
            constraint ck_recovery_deliveries_timeline
                check (
                    (available_at is null or available_at >= created_at)
                    and (expires_at is null or expires_at >= created_at)
                    and (claimed_at is null or claimed_at >= created_at))
        );

        create table recovery_delivery_items (
            recovery_delivery_id uuid not null references recovery_deliveries(id) on delete cascade,
            item_instance_id uuid not null unique references item_instances(id) on delete cascade,
            item_order integer not null,
            constraint pk_recovery_delivery_items
                primary key (recovery_delivery_id, item_instance_id),
            constraint ux_recovery_delivery_items_order
                unique (recovery_delivery_id, item_order),
            constraint ck_recovery_delivery_items_order check (item_order >= 0)
        );

        create table item_operations (
            operation_id uuid primary key,
            actor_account_id uuid null references accounts(id) on delete set null,
            actor_character_id uuid null references characters(id) on delete set null,
            operation_kind text not null,
            request_hash text not null,
            status text not null default 'pending',
            request_payload jsonb not null,
            result_payload jsonb null,
            revision bigint not null default 0,
            created_at timestamptz not null default now(),
            completed_at timestamptz null,
            constraint ck_item_operations_kind check (length(btrim(operation_kind)) > 0),
            constraint ck_item_operations_request_hash
                check (request_hash ~ '^[0-9a-f]{64}$'),
            constraint ck_item_operations_status
                check (status in ('pending', 'committed', 'rejected')),
            constraint ck_item_operations_revision check (revision >= 0),
            constraint ck_item_operations_completion
                check (
                    (status = 'pending' and completed_at is null and result_payload is null)
                    or
                    (status in ('committed', 'rejected')
                     and completed_at is not null
                     and result_payload is not null))
        );

        create table item_operation_changes (
            id bigint generated always as identity primary key,
            operation_id uuid not null references item_operations(operation_id) on delete cascade,
            change_index integer not null,
            item_instance_id uuid null references item_instances(id) on delete set null,
            change_kind text not null,
            before_state jsonb null,
            after_state jsonb null,
            created_at timestamptz not null default now(),
            constraint ux_item_operation_changes_index unique (operation_id, change_index),
            constraint ck_item_operation_changes_index check (change_index >= 0),
            constraint ck_item_operation_changes_kind check (length(btrim(change_kind)) > 0),
            constraint ck_item_operation_changes_state
                check (before_state is not null or after_state is not null)
        );

        create table item_destructions (
            item_instance_id uuid primary key,
            definition_id text not null references item_definitions(id) on delete restrict,
            source_operation_id uuid not null references item_operations(operation_id) on delete restrict,
            quantity integer not null,
            reason text not null,
            destroyed_at timestamptz not null default now(),
            constraint ck_item_destructions_quantity check (quantity > 0),
            constraint ck_item_destructions_reason check (length(btrim(reason)) > 0)
        );

        create index ix_character_item_states_snapshot
            on character_item_states(character_id, revision);

        create index ix_item_instances_definition
            on item_instances(definition_id, id);

        create index ix_item_instances_container
            on item_instances(container_id, container_slot_index, id)
            where container_id is not null;

        create index ix_item_container_slots_container
            on item_container_slots(container_id, slot_index);

        create index ix_item_instance_policies_lookup
            on item_instance_policies(item_instance_id, policy_kind, revision)
            where status = 'active';

        create index ix_item_operations_actor_replay
            on item_operations(actor_character_id, created_at desc, operation_id);

        create index ix_item_operations_request_hash
            on item_operations(request_hash);

        create index ix_recovery_deliveries_character_queue
            on recovery_deliveries(character_id, available_at, created_at, id)
            where claimed_at is null;

        create index ix_recovery_delivery_items_delivery
            on recovery_delivery_items(recovery_delivery_id, item_order);

        create function validate_character_item_state_containers()
        returns trigger
        language plpgsql
        as $$
        begin
            if not exists (
                select 1
                from item_containers
                where id = new.permanent_inventory_container_id
                  and owner_character_id = new.character_id
                  and container_type = 'permanent_inventory'
                  and lifecycle = 'active') then
                raise exception 'Character item state has an invalid permanent inventory container.'
                    using errcode = '23514';
            end if;

            if not exists (
                select 1
                from item_containers
                where id = new.bank_container_id
                  and owner_character_id = new.character_id
                  and container_type = 'bank'
                  and lifecycle = 'active') then
                raise exception 'Character item state has an invalid bank container.'
                    using errcode = '23514';
            end if;

            if not exists (
                select 1
                from item_containers
                where id = new.secure_container_id
                  and owner_character_id = new.character_id
                  and container_type = 'secure_container'
                  and lifecycle = 'active') then
                raise exception 'Character item state has an invalid Secure Container.'
                    using errcode = '23514';
            end if;

            if not exists (
                select 1
                from item_containers
                where id = new.recovery_storage_container_id
                  and owner_character_id = new.character_id
                  and container_type = 'recovery_storage'
                  and lifecycle = 'active') then
                raise exception 'Character item state has an invalid Recovery Storage container.'
                    using errcode = '23514';
            end if;

            return new;
        end;
        $$;

        create trigger trg_character_item_states_validate_containers
            before insert or update of
                character_id,
                permanent_inventory_container_id,
                bank_container_id,
                secure_container_id,
                recovery_storage_container_id
            on character_item_states
            for each row
            execute function validate_character_item_state_containers();

        create function bootstrap_character_item_state(p_character_id uuid)
        returns void
        language plpgsql
        set search_path = public, pg_temp
        as $$
        declare
            v_account_id uuid;
            v_catalog_id text;
            v_base_tier_id text;
            v_secure_tier_id text;
            v_base_carry_capacity bigint;
            v_inventory_slot_count integer;
            v_bank_slot_count integer;
            v_secure_slot_count integer;
            v_inventory_container_id uuid;
            v_bank_container_id uuid;
            v_secure_container_id uuid;
            v_recovery_container_id uuid;
        begin
            select account_id
            into v_account_id
            from characters
            where id = p_character_id
              and deleted_at is null;

            if v_account_id is null then
                raise exception 'Cannot bootstrap item state for a missing or deleted character.'
                    using errcode = '23503';
            end if;

            select
                settings.catalog_id,
                settings.base_carry_capacity,
                settings.permanent_inventory_slot_count,
                settings.bank_slot_count
            into
                v_catalog_id,
                v_base_carry_capacity,
                v_inventory_slot_count,
                v_bank_slot_count
            from item_system_settings settings
            where settings.id = 'character_default';

            select revision.base_secure_container_tier_id
            into v_base_tier_id
            from item_catalog_revisions revision
            where revision.catalog_id = v_catalog_id
              and revision.is_current;

            if v_base_tier_id is null then
                raise exception 'Cannot bootstrap character item state without a current item catalog.'
                    using errcode = '23514';
            end if;

            insert into account_secure_container_entitlements (account_id, tier_id)
            values (v_account_id, v_base_tier_id)
            on conflict (account_id) do nothing;

            select entitlement.tier_id, tier.slot_capacity
            into v_secure_tier_id, v_secure_slot_count
            from account_secure_container_entitlements entitlement
            join secure_container_tiers tier on tier.id = entitlement.tier_id
            where entitlement.account_id = v_account_id
              and tier.is_active;

            if v_secure_tier_id is null then
                raise exception 'Cannot bootstrap character item state without an active Secure Container tier.'
                    using errcode = '23514';
            end if;

            insert into item_containers (
                id,
                container_type,
                owner_character_id,
                slot_capacity)
            values (
                gen_random_uuid(),
                'permanent_inventory',
                p_character_id,
                v_inventory_slot_count)
            on conflict do nothing;

            insert into item_containers (
                id,
                container_type,
                owner_character_id,
                slot_capacity)
            values (
                gen_random_uuid(),
                'bank',
                p_character_id,
                v_bank_slot_count)
            on conflict do nothing;

            insert into item_containers (
                id,
                container_type,
                owner_character_id,
                slot_capacity)
            values (
                gen_random_uuid(),
                'secure_container',
                p_character_id,
                v_secure_slot_count)
            on conflict do nothing;

            insert into item_containers (
                id,
                container_type,
                owner_character_id,
                slot_capacity)
            values (
                gen_random_uuid(),
                'recovery_storage',
                p_character_id,
                null)
            on conflict do nothing;

            select id
            into v_inventory_container_id
            from item_containers
            where owner_character_id = p_character_id
              and container_type = 'permanent_inventory'
              and lifecycle = 'active';

            select id
            into v_bank_container_id
            from item_containers
            where owner_character_id = p_character_id
              and container_type = 'bank'
              and lifecycle = 'active';

            select id
            into v_secure_container_id
            from item_containers
            where owner_character_id = p_character_id
              and container_type = 'secure_container'
              and lifecycle = 'active';

            select id
            into v_recovery_container_id
            from item_containers
            where owner_character_id = p_character_id
              and container_type = 'recovery_storage'
              and lifecycle = 'active';

            insert into item_container_slots (container_id, slot_index, slot_kind)
            select v_inventory_container_id, slot_index, 'general'
            from generate_series(0, v_inventory_slot_count - 1) as slot_index
            on conflict do nothing;

            insert into item_container_slots (container_id, slot_index, slot_kind)
            select v_bank_container_id, slot_index, 'general'
            from generate_series(0, v_bank_slot_count - 1) as slot_index
            on conflict do nothing;

            insert into item_container_slots (container_id, slot_index, slot_kind)
            select v_secure_container_id, slot_index, 'general'
            from generate_series(0, v_secure_slot_count - 1) as slot_index
            on conflict do nothing;

            insert into character_item_states (
                character_id,
                carried_weight,
                base_carry_capacity,
                carry_capacity,
                permanent_inventory_container_id,
                bank_container_id,
                secure_container_id,
                recovery_storage_container_id)
            values (
                p_character_id,
                0,
                v_base_carry_capacity,
                v_base_carry_capacity,
                v_inventory_container_id,
                v_bank_container_id,
                v_secure_container_id,
                v_recovery_container_id)
            on conflict (character_id) do nothing;
        end;
        $$;
        """;
}
