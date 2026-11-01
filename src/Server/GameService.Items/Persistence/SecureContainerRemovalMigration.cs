namespace Mmorpg.GameService.Items.Persistence;

internal static class SecureContainerRemovalMigration
{
    public const string Id = "202611011200_remove_secure_container";

    // The Secure Container leaves the game. Its items move into Recovery Storage as one
    // delivery per character, so no item is lost, and the startup carry recompute drops
    // their weight. Removing Secure Container eligibility from item definitions changes
    // every structural fingerprint, so the stored fingerprints of the core catalog
    // definitions as they were before are restamped to their new values. A definition
    // with any other stored fingerprint keeps it, and the catalog seeder still refuses a
    // structural change of a definition with live instances.
    public const string Sql = """
        do $$
        declare
            v_state record;
            v_item record;
            v_delivery_id uuid;
            v_next_slot_index integer;
            v_item_order integer;
        begin
            for v_state in
                select
                    state.character_id,
                    state.secure_container_id,
                    state.recovery_storage_container_id
                from character_item_states state
                where exists (
                    select 1
                    from item_instances item
                    where item.container_id = state.secure_container_id)
                order by state.character_id
            loop
                v_delivery_id := gen_random_uuid();
                insert into recovery_deliveries (
                    id,
                    character_id,
                    recovery_storage_container_id,
                    source_kind,
                    source_event_id,
                    available_at)
                values (
                    v_delivery_id,
                    v_state.character_id,
                    v_state.recovery_storage_container_id,
                    'secure_container_removal',
                    '202611011200_remove_secure_container',
                    now());

                select coalesce(max(slot_index) + 1, 0)
                into v_next_slot_index
                from item_container_slots
                where container_id = v_state.recovery_storage_container_id;

                v_item_order := 0;
                for v_item in
                    select id
                    from item_instances
                    where container_id = v_state.secure_container_id
                    order by container_slot_index, id
                loop
                    insert into item_container_slots (container_id, slot_index, slot_kind)
                    values (
                        v_state.recovery_storage_container_id,
                        v_next_slot_index + v_item_order,
                        'general');

                    update item_instances
                    set container_id = v_state.recovery_storage_container_id,
                        container_slot_index = v_next_slot_index + v_item_order,
                        revision = revision + 1,
                        updated_at = now()
                    where id = v_item.id;

                    insert into recovery_delivery_items (
                        recovery_delivery_id,
                        item_instance_id,
                        item_order)
                    values (v_delivery_id, v_item.id, v_item_order);

                    v_item_order := v_item_order + 1;
                end loop;

                update item_containers
                set revision = revision + 1,
                    updated_at = now()
                where id = v_state.recovery_storage_container_id;

                update character_item_states
                set revision = revision + 1,
                    updated_at = now()
                where character_id = v_state.character_id;
            end loop;

            -- Deleting a container deletes its items, so none may be left behind.
            if exists (
                select 1
                from item_instances item
                join item_containers container on container.id = item.container_id
                where container.container_type = 'secure_container') then
                raise exception 'Secure Container items remain after the move to Recovery Storage.';
            end if;
        end;
        $$;

        -- The move leaves deferred foreign key checks pending, and a table with pending
        -- trigger events cannot be altered, so they run now.
        set constraints all immediate;

        drop trigger trg_character_item_states_validate_containers on character_item_states;

        alter table character_item_states
            drop constraint ck_character_item_states_distinct_containers,
            drop column secure_container_id;

        alter table character_item_states
            add constraint ck_character_item_states_distinct_containers
                check (
                    permanent_inventory_container_id <> bank_container_id
                    and permanent_inventory_container_id <> recovery_storage_container_id
                    and bank_container_id <> recovery_storage_container_id);

        delete from item_containers where container_type = 'secure_container';

        alter table item_containers
            drop constraint ck_item_containers_type,
            drop constraint ck_item_containers_binding;

        alter table item_containers
            add constraint ck_item_containers_type
                check (container_type in (
                    'permanent_inventory',
                    'bank',
                    'recovery_storage',
                    'bag_contents',
                    'corpse_inventory',
                    'corpse_equipment',
                    'corpse_bag_contents')),
            add constraint ck_item_containers_binding
                check (
                    (
                        container_type in (
                            'permanent_inventory',
                            'bank',
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
                    ));

        drop index ux_item_containers_active_character_type;

        create unique index ux_item_containers_active_character_type
            on item_containers(owner_character_id, container_type)
            where lifecycle = 'active'
              and owner_character_id is not null
              and container_type in (
                  'permanent_inventory',
                  'bank',
                  'recovery_storage');

        create or replace function validate_character_item_state_containers()
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
                recovery_storage_container_id
            on character_item_states
            for each row
            execute function validate_character_item_state_containers();

        create or replace function bootstrap_character_item_state(p_character_id uuid)
        returns void
        language plpgsql
        set search_path = public, pg_temp
        as $$
        declare
            v_catalog_id text;
            v_base_carry_capacity bigint;
            v_inventory_slot_count integer;
            v_bank_slot_count integer;
            v_inventory_container_id uuid;
            v_bank_container_id uuid;
            v_recovery_container_id uuid;
        begin
            if not exists (
                select 1
                from characters
                where id = p_character_id
                  and deleted_at is null) then
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

            if not exists (
                select 1
                from item_catalog_revisions revision
                where revision.catalog_id = v_catalog_id
                  and revision.is_current) then
                raise exception 'Cannot bootstrap character item state without a current item catalog.'
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

            insert into character_item_states (
                character_id,
                carried_weight,
                base_carry_capacity,
                carry_capacity,
                permanent_inventory_container_id,
                bank_container_id,
                recovery_storage_container_id)
            values (
                p_character_id,
                0,
                v_base_carry_capacity,
                v_base_carry_capacity,
                v_inventory_container_id,
                v_bank_container_id,
                v_recovery_container_id)
            on conflict (character_id) do nothing;
        end;
        $$;

        delete from corpse_snapshots where snapshot_kind = 'secure_container';

        alter table corpse_snapshots
            drop constraint ck_corpse_snapshots_kind,
            drop constraint ck_corpse_snapshots_shape;

        alter table corpse_snapshots
            add constraint ck_corpse_snapshots_kind
                check (snapshot_kind in (
                    'insured_equipment',
                    'protected_bag',
                    'insured_bag')),
            add constraint ck_corpse_snapshots_shape
                check (
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
                    ));

        drop table account_secure_container_entitlements;
        drop table secure_container_tiers;
        drop table item_definition_location_rules;

        alter table item_catalog_revisions drop column base_secure_container_tier_id;

        update item_definitions definition
        set structural_fingerprint = restamp.new_fingerprint
        from (values
            ('ammunition.bronze_arrow', 'fa06e0a7bc54db1272427fc42b47209a84690c5c7c919fe41c759f9e3cf7ca5e', '5f1991cc3bd1f460883f242d5f71e28770688475c3ea44085947d28827cae0a1'),
            ('armor.heater_shield', '76118d0319a2449e7cde49dc5dd9bcc39c23368bfda16938a34907990c0ce69b', '049672274bd86f08846006a191daf29cca19270cdf5c727428ce61e60621422b'),
            ('armor.leather_boots', '1413130ba55887a19c90ee3f4c11cf2c76a04e6ed49624435d0002567e883286', '6e2928df8fa2e172a921aecc72b7f11caed008862f96b9e2d28194749780fcd1'),
            ('armor.leather_gloves', '50e6d22b994e1bcda009accb56ad482e8306a2bc07b81e2bbd315f5fb4cf6169', 'ef72212e9cea15bbba2ab3434a95236e74a620a9ced29d2f175007bdc71ef471'),
            ('armor.leather_jerkin', 'c9d11b020651869e3ae8bf1bb79d171548904dae1780f5ea0c862f8a53ad389f', 'd19d8044813f3687211a6008d9ead441a0b96f3629f8c1fe891fc66af92607dd'),
            ('armor.nasal_helm', '7a42bf8da658b4a55c5117cce81bc0577e9342c8ce05fd36755ca17c3f3f5c7b', '74f8190c2e4c8752b41cac3856bc21f8f3edb228d250f64717a6786241290ee2'),
            ('bag.travelers_pack', 'c1278a32aa3887b0018bb55984230b9ee3e6dea83030f75ee9d5dfe97acd5680', '2fd9ca7df93f61dc9c960e556ca0481aa4bf1da2da385869b46d83c0d43f775c'),
            ('clothing.blue_wool_cloak', 'f5713b8db468b25dcbca0013f8ea9fae36fb0b63bfef38929f643f7db25fb777', '9c6014810905415494c9b20b3df04aa777e475911f1fdbe0e58c453810b8e641'),
            ('clothing.blue_wool_hood', '02b8f5d9a9ffca88bc058030d74ae5a8b3c3ede0d7147430d8b74d3d1fc93868', 'dac9f7042c7caf5d353e5e422f0520ee67e3ea17a4fbbf2f049c446b4f7f8cc1'),
            ('clothing.blue_wool_robe', '4e4bb72fbf363a1c6634316b5c088fd9ec70a63a62fcec4bf53de20cef003335', 'fe9d5302df2b55c2b62213b879e3fddafc0c93332d7e6b9d353cabf1a8b08c7f'),
            ('clothing.linen_shirt', '58b994962a21bea749106b067160d642ccf4e30f20b58cb06369dfbf48276bf2', 'b1bb22a8f9ec0c4ce2472814322bdba4b19bc22a0f34e282fb38eabb118683f4'),
            ('clothing.long_linen_shirt', '516a24b906cc11c30186109fa8968c44d64ec194855a3919ac2e71ac5944944c', '326699d60c6ad2aa80f866eb6adca8eca83e7042bcc1c034e0ba2528d4d32b04'),
            ('clothing.net_shirt', 'e1b89b08188df264a4e0715505e02783c795d3aae84e758fb7ac754bce857289', '4a01c4d7a380cf777750ed06e742575f295887f104f2fa7464ae1ced37a40b2d'),
            ('clothing.red_wool_cloak', '365493de275eca641878647a6d40d7d84d20ca3095b7abdf2274a9f6633d2c60', '01a030b667e0e474918fb341b72b4f98a32fd0365bda81c026b39e6946e3c80e'),
            ('clothing.red_wool_hood', '3836c719947b81357523741de4583d6a4199327116fac25e2f32609d270e5348', 'cbaf71b93349cea2ead97ca99a13ce413d01ac16aab08ab19e0bfd8c0659c895'),
            ('clothing.red_wool_robe', '19f3f7fcb8bc3b175290f9d71bc4c2a8b91e8ff55e04ebb80307c8918cbcfa9b', 'ca361973f000e5199ab11decdbcdbba4012b036c80ce7de8e7b975cdc651cba3'),
            ('clothing.wool_cloak', 'cc3e68a1d8d2cd7fba91bc90e5edf838c86987e7d890895916e08dacc02b9235', '7cda73642793f84b2d8bb5056a2dee9155163c4c1835dd1b8b0b4f4b97e2e88f'),
            ('clothing.wool_hood', '94d5e2be2e59e59bd81e5c2ba1594d59bd8d42fe84bd4d64f5875ffc5c2bd84c', '8fa3506052f11e1887cf5abc7a4b99c0d0fc9dee7a7c6aa3ea148ad0ca2305c1'),
            ('clothing.wool_robe', '8555a6fc0b43675e4b22921e6cc2458d2bf08d3366a013588bf537abce6516b5', '33a66113908df36f97e338063335e645f9a77da2bf0874f15f0dc8eb970ed32d'),
            ('clothing.wool_trousers', 'e15717533031ca42b08fe649be1b0e5311c5fae0675a3e5a855afa746ffb51b1', '8450c3b2044ff44ad40b369e088c450569987be1d2a2bdfc8606618627660a5a'),
            ('food.cooked_trout', '0a14fa2f9dca4eebec1565fa0f185ff89ae5600e770a361e12a705ee5fb77499', '2693c55f6e87bdd709fdd799e12f366b1b6c21f07c0171a31fa4dbc066417d86'),
            ('jewelry.copper_ring', 'f110fde13edbe2470eab2af9ad7897a8f948ea8814ccb1ac12edb1229c18cfdb', '9c7e92db14a727e2a24a42eefa59b3daff5d2e26fc6fc478c3ef6ff7527c0895'),
            ('material.iron_ore', '929a4e984b8982ab049ee54fe011d09a8e8d2f2f8f931242e5813f5f00de4df6', '9053eaca4c9c2ccbb95b88858a4da7761db8cbbc9b7fad283561f1383050ddd6'),
            ('material.oak_log', '680ce657678cc628381d10039a6951b7779070fe781ab30b80284b0b8e086a7a', 'eec750ca8e35084b05175fda6c9f917cf504b636c9269e64ee82706bad822402'),
            ('material.wolf_pelt', '3494fe310c70df42374200eec525f293206dcb074f0f051b7e604fd0d7752617', '1f1967f98ea08eba2af80f6da565da9cec8e4373eacd9ea1c11e5cb39750050b'),
            ('potion.minor_healing', '6ba213d79c76a2284c1fe912a2129bee6b97c4b3e4f5c37abbe1b6e68eed95dc', '18e8cf6b700e269bdf5c485fa5e2d60623be81928cc02c4928b590989e519863'),
            ('quest_item.sealed_letter', '3caed9e50ded159ed28151e4b60e18f69cedb0021c7ef9c7cd83d33e59fdc508', '822d9e7fa96830059a71513c833d663bdc0f8d9de361bea762aff418bb11b444'),
            ('tool.starter_pickaxe', 'f5e00ea2809b4aeabf79ae97c1c0b09d9d360f6dca98579bb05a85392eadefef', '30832499d8f3bbb2bd2a827da4b3a895645fee662fb378f8bd5d2aa0db63efd5'),
            ('tool.woodcutting_axe', '63e4c4b7fcc297287a9ce7a3198f46c93c17aad865b0b3632fdf7f292b11fa3e', 'a80f60279a332e58634cf4169e014f13875747d192929ef33e7f0593d8b15015'),
            ('weapon.iron_greataxe', '440d6affe93e42d8ed9a33598cd4d538ce5627665b19552aedaf0c8af3f5688d', '1f73a7c7871ea5e431a0f29b3b21f46bd31f93742fc276a4f3796d74b1501ae7'),
            ('weapon.iron_sword', '52509c503e8ac36450e02072864ef28566b6223804280c0a5a0b0f70b8c34bcf', '5ef67777a6bf9e234523ec5548fcc519ab9c225e15ae2fdf6e708dbc74d0c12e'),
            ('weapon.oak_staff', 'a2588a67b019564050760ce8e11ebaab82bd1e2bfaec7ddcb64a9869206743ae', '0f5c6ca9c3780a5ac40993490bd3e3358015756aaa13a4154fd38fe91c282087'),
            ('weapon.training_shortbow', 'f062a6af3f7a41e821539b89a50408d07abd5a6577548fe54e7167c749542732', '1b88be65bb0f83ee0d89743e20df4dcda649cd6ef76e53d05d088c9d0893b708'))
            as restamp(id, old_fingerprint, new_fingerprint)
        where definition.id = restamp.id
          and definition.structural_fingerprint = restamp.old_fingerprint;
        """;
}
