namespace TbotUltra.Mac;

/// <summary>
/// The Mac app's own starting values for the engine options its screens expose. They are applied
/// under the user's saved options and sent to the UI, so what a control shows is always what the
/// engine actually runs with — independent of the engine's internal fallbacks.
/// </summary>
public static class MacDefaults
{
    public static IReadOnlyDictionary<string, string> Options { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["hero_min_hp_for_adventure"] = "50",
        ["hero_adventure_pick_order"] = "shortest",
        ["hero_auto_revive"] = "true",

        // The engine waits a random time before each adventure. Its own range is long, and the wait
        // starts over whenever the browser is reopened, so the Mac app keeps it short.
        ["hero_adventure_restart_delay_enabled"] = "true",
        ["hero_adventure_restart_delay_min_minutes"] = "1",
        ["hero_adventure_restart_delay_max_minutes"] = "4",
        ["mac_keep_awake"] = "true",
        ["hero_auto_assign_points"] = "true",
        ["hero_auto_use_ointments"] = "false",
        ["hero_stat_priority"] = "resources,fighting_strength,offence_bonus,defence_bonus",
        ["hero_stat_maximums"] = "resources=100,fighting_strength=100,offence_bonus=100,defence_bonus=100",
        ["hero_resource_use_construction"] = "true",
        ["hero_resource_use_smithy"] = "true",
        ["hero_resource_use_town_hall"] = "true",
        ["increase_adventures_to_hard"] = "false",
        ["reduce_adventure_time"] = "false",
        ["hero_adventure_video_chance_percent"] = "70",
        ["continuous_farm_send_mode"] = "shared_schedule",
        ["continuous_farm_dispatch_delay_min_minutes"] = "30",
        ["continuous_farm_dispatch_delay_max_minutes"] = "90",
        ["continuous_farm_deactivate_red_losses"] = "true",
        ["continuous_farm_deactivate_yellow_losses"] = "true",
        ["continuous_farm_deactivate_red_oasis_losses"] = "false",
        ["continuous_farm_deactivate_yellow_oasis_losses"] = "false",
        ["continuous_farm_move_red_losses"] = "false",
        ["continuous_farm_move_yellow_losses"] = "false",
        ["resource_build_strategy"] = "lowest_first",
        ["construction_humanize_delay_enabled"] = "true",
        ["action_pacing_enabled"] = "true",
        ["npc_trade_enabled"] = "false",
        ["npc_trade_construction_enabled"] = "false",
        ["npc_trade_threshold_percent"] = "90",
        ["npc_trade_analyze_wood"] = "true",
        ["npc_trade_analyze_clay"] = "true",
        ["npc_trade_analyze_iron"] = "true",
        ["npc_trade_analyze_crop"] = "true",
        ["npc_trade_build_time_limit_enabled"] = "true",
        ["npc_trade_build_time_limit_seconds"] = "300",
        ["allow_gold_spending"] = "false",
        ["gold_limit"] = "100",
        ["daily_gold_spending_limit"] = "20",
        ["construct_faster_enabled"] = "true",
        ["construct_faster_min_build_time_enabled"] = "true",
        ["construct_faster_min_build_minutes"] = "30",
        ["construct_faster_random_enabled"] = "false",
        ["construct_faster_random_chance_percent"] = "50",
        ["turn_off_video_sound"] = "true",
        ["resource_transfer_source_threshold_percent"] = "50",
        ["resource_transfer_source_keep_percent"] = "5",
        ["resource_transfer_target_fill_percent"] = "90",
        ["reinforcements_send_min_minutes"] = "60",
        ["reinforcements_send_max_minutes"] = "120",

        ["mac_storage_preflight"] = "true",
        ["mac_auto_cropland"] = "true",

        // Under attack: what to do shortly before a hit lands.
        ["mac_attack_notify"] = "true",
        ["mac_attack_spend_enabled"] = "true",
        ["mac_attack_spend_lead_minutes"] = "15",
        ["mac_evasion_enabled"] = "false",
        ["mac_evasion_target"] = "",
        ["mac_evasion_include_hero"] = "true",
        ["mac_evasion_lead_minutes"] = "6",

        // Celebrations exist to earn culture points, so by default they pause once there are enough.
        ["mac_celebrate_only_when_cp_short"] = "true",
        ["town_hall_celebration_mode"] = "small",

        // Daily summary notification.
        ["mac_daily_summary_enabled"] = "true",
        ["mac_daily_summary_hour"] = "21",

        // Raid sizing from reports.
        ["mac_raid_sizing_enabled"] = "true",

        // Farm growth: find empty oases nearby and add them to a farm list.
        ["mac_autofarm_enabled"] = "false",
        ["mac_autofarm_radius"] = "10",
        ["mac_autofarm_list"] = "Auto oases",
        ["mac_autofarm_troop"] = "",
        ["mac_autofarm_troop_count"] = "20",
        ["mac_autofarm_max_per_run"] = "3",
        ["mac_autofarm_interval_hours"] = "12",

        // Overflow guard: spend resources before storage caps.
        ["mac_overflow_guard_enabled"] = "true",
        ["mac_overflow_threshold_percent"] = "90",
        ["hero_ointment_target_hp_percent"] = "100",

        // Session pacing is the Mac loop's own: play for a while, close the browser, come back later.
        ["mac_pacing_enabled"] = "true",
        ["mac_pacing_run_min_minutes"] = "60",
        ["mac_pacing_run_max_minutes"] = "150",
        ["mac_pacing_sleep_min_minutes"] = "15",
        ["mac_pacing_sleep_max_minutes"] = "45",
        ["mac_active_from_hour"] = "0",
        ["mac_active_to_hour"] = "24",
    };
}
