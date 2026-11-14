using System;
using GTA;
using GTA.Math;
using GTA.Native;

// SurvivalMoves v5 - ผูกกับปุ่มที่พอร์ตมีอยู่แล้ว ทำงานตั้งแต่เข้าเกม ไม่มีเมนู ไม่ต้องเปิด/ปิด
//
//   พันแผล  = ค้าง Aim (LT) แล้วแตะ Cover (RB)
//   ใส่เกราะ = ค้าง Aim (LT) แล้วแตะปุ่มอาวุธ (LB)
//   สไลด์   = ตอนวิ่ง (Sprint) แตะ Stealth (L3)
//   คอมโบ LT ใช้ได้เฉพาะตอนมือเปล่า (ตั้ง ONLY_WHEN_UNARMED = false ถ้าอยากใช้ตลอด)
//
// สคริปต์นี้ไม่อ่าน/ไม่ปิดปุ่ม D-pad และปุ่ม Accept/Cancel เลย
// เพื่อไม่ให้พอร์ตเข้าใจผิดว่ามีเมนูเปิดอยู่ (แผงลูกศร + X + ถูก บนจอ)
public class SurvivalMoves : Script
{
    // ---------- ผูกปุ่ม (Control ID ของเกม; ใส่ได้หลายตัว) ----------
    static readonly int[] MOD_IDS     = { (int)Control.Aim };          // LT  ค้างไว้
    static readonly int[] BANDAGE_IDS = { (int)Control.Cover };        // RB  แตะ
    static readonly int[] ARMOR_IDS   = { (int)Control.SelectWeapon }; // LB  แตะ
    static readonly int[] SLIDE_IDS   = { (int)Control.Duck };         // L3 / Stealth  แตะตอนวิ่ง

    const bool ONLY_WHEN_UNARMED = true;
    const bool LEARN_MODE = false;   // true = ขึ้นเลข Control ID ของปุ่มที่แตะ (ไม่สแกนปุ่มเมนู)

    // ---------- ค่าต่างๆ ----------
    const int   BANDAGE_TOTAL   = 60;
    const int   BANDAGE_STEP    = 10;
    const int   BANDAGE_TICK_MS = 500;
    const int   BANDAGE_BASE_MS = 900;   // ช่วงท่า base ก่อนสลับเป็น idle_a
    const int   ARMOR_ANIM_MS   = 1800;
    const int   SLIDE_MS        = 700;   // ช่วงกำลังสไลด์ (run_slide)
    const int   SLIDE_END_MS    = 450;   // ช่วงลุกขึ้นวิ่งต่อ (run_slide_end)
    const float SLIDE_SPEED     = 8f;
    const float SLIDE_RUN_SPEED = 4f;    // ความเร็วตอนลุกขึ้นวิ่งต่อ

    // ท่าพันแผล: base (เริ่ม) แล้วต่อด้วย idle_a
    const string BANDAGE_DICT = "amb@medic@standing@tendtodead@base";
    const string BANDAGE_ANIM = "base";
    const string IDLE_DICT    = "amb@medic@standing@tendtodead@idle_a";
    const string IDLE_ANIM    = "idle_a";

    const string ARMOR_DICT = "clothingtie";
    const string ARMOR_ANIM = "try_tie_positive_a";

    // ท่าสไลด์: จับคู่ dict กับคลิปตามลำดับ (ตัวที่ 0 คู่กับตัวที่ 0 ...)
    static readonly string[] SLIDE_DICTS = { "anim@arena@celeb@flat@solo@no_props@", "missheistfbi3b_ig6_v2" };
    static readonly string[] SLIDE_CLIPS = { "slide_a_player_a", "rubble_slide_franklin" };
    const int SLIDE_PICK = -1;   // -1 = ใช้คู่แรกที่มีในเกม, 0 = บังคับ slide_a_player_a, 1 = บังคับ rubble_slide_franklin
    const string SLIDE_FALLBACK_DICT = "move_jump";
    const string SLIDE_FALLBACK_ANIM = "dive_start_run";
    // -------------------------------------------

    // Control ID สำหรับโหมดเรียนรู้ (ไม่รวมปุ่มเมนู/D-pad 166-178)
    static readonly int[] LEARN_IDS = BuildLearnIds();

    bool introShown; int introUntil;
    int learnUntil; string learnText = "";

    bool bandaging; int healLeft; int nextHeal; int bandageStart; int bandageEnd; int bandagePhase;
    bool armoring; int armorEnd;
    bool sliding; int slideStart; int slideEndStart; int slidePhase; int slideIdx = -1; Vector3 slideDir;

    public SurvivalMoves()
    {
        Tick += OnTick;
    }

    static int[] BuildLearnIds()
    {
        var list = new System.Collections.Generic.List<int>();
        for (int i = 14; i <= 60; i++) list.Add(i);
        for (int i = 140; i <= 146; i++) list.Add(i);
        list.AddRange(new[] { 192, 193, 244, 245, 249, 257, 261, 262, 289, 290, 296, 297, 298, 299 });
        return list.ToArray();
    }

    bool JustPressed(int id)
    {
        return Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, id);
    }

    bool Held(int id)
    {
        return Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, id);
    }

    bool AnyJust(int[] ids)
    {
        for (int i = 0; i < ids.Length; i++) if (JustPressed(ids[i])) return true;
        return false;
    }

    bool AnyHeld(int[] ids)
    {
        for (int i = 0; i < ids.Length; i++) if (Held(ids[i])) return true;
        return false;
    }

    void DisableAll(int[] ids)
    {
        for (int i = 0; i < ids.Length; i++)
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, ids[i], true);
    }

    bool LoadDict(string dict)
    {
        if (string.IsNullOrEmpty(dict)) return false;
        if (!Function.Call<bool>(Hash.DOES_ANIM_DICT_EXIST, dict)) return false;
        Function.Call(Hash.REQUEST_ANIM_DICT, dict);
        int tries = 0;
        while (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict) && tries++ < 50)
            Script.Yield();
        return Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict);
    }

    bool PlayAnim(Ped p, string dict, string anim, int ms, int flag)
    {
        if (!LoadDict(dict)) return false;
        Function.Call(Hash.TASK_PLAY_ANIM, p, dict, anim, 8f, -8f, ms, flag, 0f, false, false, false);
        return true;
    }

    void StopAnim(Ped p, string dict, string anim)
    {
        if (string.IsNullOrEmpty(dict)) return;
        Function.Call(Hash.STOP_ANIM_TASK, p, dict, anim, 1f);
    }

    void StopBandageAnims(Ped p)
    {
        StopAnim(p, BANDAGE_DICT, BANDAGE_ANIM);
        StopAnim(p, IDLE_DICT, IDLE_ANIM);
    }

    void StopSlideAnims(Ped p)
    {
        for (int i = 0; i < SLIDE_DICTS.Length; i++) StopAnim(p, SLIDE_DICTS[i], SLIDE_CLIPS[i]);
        StopAnim(p, SLIDE_FALLBACK_DICT, SLIDE_FALLBACK_ANIM);
    }

    void Learn(int now)
    {
        string found = "";
        for (int i = 0; i < LEARN_IDS.Length; i++)
            if (JustPressed(LEARN_IDS[i])) found += (found.Length > 0 ? ", " : "") + LEARN_IDS[i];
        if (found.Length > 0) { learnText = "ctrl " + found; learnUntil = now + 2500; }
        if (now < learnUntil) GTA.UI.Screen.ShowSubtitle(learnText, 100);
    }

    void OnTick(object sender, EventArgs e)
    {
        Ped p = Game.Player.Character;
        bool alive = p != null && p.Exists() && !p.IsDead;
        int now = Game.GameTime;
        if (!alive) { sliding = false; return; }

        if (LEARN_MODE) Learn(now);

        if (!introShown) { introShown = true; introUntil = now + 6000; }
        if (now < introUntil && !(LEARN_MODE && now < learnUntil))
            GTA.UI.Screen.ShowSubtitle("SurvivalMoves: hold Aim + Cover = bandage, Aim + Weapon = armor, Sprint + Stealth = slide", 100);

        if (p.IsInVehicle()) { sliding = false; return; }

        // ---------- คอมโบกับปุ่มของพอร์ต ----------
        bool comboOk = !ONLY_WHEN_UNARMED || p.Weapons.Current.Hash == WeaponHash.Unarmed;
        bool mod = comboOk && AnyHeld(MOD_IDS);
        if (mod)
        {
            DisableAll(BANDAGE_IDS);   // กัน Cover / วงล้ออาวุธ ทำงานซ้อน
            DisableAll(ARMOR_IDS);
        }
        bool sprinting = p.IsSprinting;
        if (sprinting) DisableAll(SLIDE_IDS);   // กัน Stealth สลับโหมดตอนวิ่ง

        if (mod && AnyJust(BANDAGE_IDS)) StartBandage(p, now);
        if (mod && AnyJust(ARMOR_IDS)) StartArmor(p, now);
        if (sprinting && AnyJust(SLIDE_IDS)) StartSlide(p, now);

        // ---------- พันแผล ----------
        if (bandaging)
        {
            if (bandagePhase == 0 && now - bandageStart >= BANDAGE_BASE_MS)
            {
                bandagePhase = 1;
                if (!PlayAnim(p, IDLE_DICT, IDLE_ANIM, Math.Max(500, bandageEnd - now), 49))
                    PlayAnim(p, BANDAGE_DICT, BANDAGE_ANIM, Math.Max(500, bandageEnd - now), 49);
            }
            if (now >= nextHeal && healLeft > 0)
            {
                p.Health = Math.Min(p.MaxHealth, p.Health + BANDAGE_STEP);
                healLeft -= BANDAGE_STEP;
                nextHeal = now + BANDAGE_TICK_MS;
            }
            if (healLeft <= 0 || now >= bandageEnd || p.Health >= p.MaxHealth)
            {
                bandaging = false;
                StopBandageAnims(p);
            }
        }

        // ---------- ใส่เกราะ ----------
        if (armoring && now >= armorEnd)
        {
            armoring = false;
            p.Armor = 100;
            StopAnim(p, ARMOR_DICT, ARMOR_ANIM);
            GTA.UI.Screen.ShowSubtitle("Armor on");
        }

        // ตัวถึกขึ้นเมื่อมีเกราะ: ไม่โดนฮิตคริติคอล และล้มยากขึ้น
        bool armored = p.Armor > 0;
        Function.Call(Hash.SET_PED_SUFFERS_CRITICAL_HITS, p, !armored);
        Function.Call(Hash.SET_PED_CAN_RAGDOLL_FROM_PLAYER_IMPACT, p, !armored);

        // ---------- สไลด์ ----------
        if (sliding)
        {
            if (slidePhase == 0)
            {
                int t = now - slideStart;
                if (t >= SLIDE_MS)
                {
                    // เปลี่ยนเป็นช่วงลุกขึ้นวิ่งต่อ: หยุดท่าสไลด์ให้ตัวละครลุกกลับมาวิ่ง
                    slidePhase = 1;
                    slideEndStart = now;
                    StopSlideAnims(p);
                }
                else
                {
                    float speed = SLIDE_SPEED * (1f - (float)t / SLIDE_MS) + SLIDE_RUN_SPEED;
                    Vector3 v = slideDir * speed;
                    v.Z = p.Velocity.Z;
                    p.Velocity = v;
                }
            }
            if (slidePhase == 1)
            {
                if (now - slideEndStart >= SLIDE_END_MS)
                {
                    sliding = false;
                    StopSlideAnims(p);
                }
                else
                {
                    Vector3 v = slideDir * SLIDE_RUN_SPEED;
                    v.Z = p.Velocity.Z;
                    p.Velocity = v;
                }
            }
        }
    }

    void StartBandage(Ped p, int now)
    {
        if (bandaging || sliding) return;
        if (p.Health >= p.MaxHealth) { GTA.UI.Screen.ShowSubtitle("Health is already full"); return; }
        bandaging = true;
        bandagePhase = 0;
        healLeft = BANDAGE_TOTAL;
        bandageStart = now;
        nextHeal = now + BANDAGE_TICK_MS;
        bandageEnd = now + (BANDAGE_TOTAL / BANDAGE_STEP) * BANDAGE_TICK_MS;
        PlayAnim(p, BANDAGE_DICT, BANDAGE_ANIM, BANDAGE_BASE_MS, 49);
    }

    void StartArmor(Ped p, int now)
    {
        if (armoring || sliding) return;
        if (p.Armor >= 100) { GTA.UI.Screen.ShowSubtitle("Armor is already full"); return; }
        armoring = true;
        armorEnd = now + ARMOR_ANIM_MS;
        PlayAnim(p, ARMOR_DICT, ARMOR_ANIM, ARMOR_ANIM_MS, 49);
    }

    void StartSlide(Ped p, int now)
    {
        if (sliding) return;
        if (armoring) { armoring = false; StopAnim(p, ARMOR_DICT, ARMOR_ANIM); }
        if (bandaging) { bandaging = false; StopBandageAnims(p); }
        sliding = true;
        slidePhase = 0;
        slideStart = now;
        slideDir = p.ForwardVector;
        // เลือกท่าสไลด์: บังคับด้วย SLIDE_PICK หรือใช้คู่แรกที่มี dict ในเกม
        slideIdx = -1;
        if (SLIDE_PICK >= 0 && SLIDE_PICK < SLIDE_DICTS.Length)
        {
            if (PlayAnim(p, SLIDE_DICTS[SLIDE_PICK], SLIDE_CLIPS[SLIDE_PICK], SLIDE_MS + 200, 1)) slideIdx = SLIDE_PICK;
        }
        else
        {
            for (int i = 0; i < SLIDE_DICTS.Length && slideIdx < 0; i++)
                if (PlayAnim(p, SLIDE_DICTS[i], SLIDE_CLIPS[i], SLIDE_MS + 200, 1)) slideIdx = i;
        }
        if (slideIdx < 0)
            PlayAnim(p, SLIDE_FALLBACK_DICT, SLIDE_FALLBACK_ANIM, SLIDE_MS, 0);
    }
}
