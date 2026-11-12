using System.Collections.Generic;
using DW1Remake.Disc;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// Ours, an OPTIONS mod (EVOLUTION GUIDE, off by default): Square on a Digimon of the Chart page opens this page over the Player menu. Page 1 lists
    /// what each of its evolutions asks for (EVOLUTION_REQS 0x8012ABEC, through PartnerCare.ReqOf) next to the partner's numbers now, and the one the
    /// game's evolution check would choose (PartnerCare.Project: the same steps as getInTraining / getRookie / getChampionEvolutionTarget). Pages 2-3
    /// explain those rules (from main/evolution.c and partner_impl.c tickConditions). Nothing here changes the game.
    /// </summary>
    public sealed class EvolutionGuideView
    {
        public bool Showing { get; private set; }
        int _id, _page;
        const int Pages = 3;
        readonly List<PartnerCare.EvoScore> _log = new List<PartnerCare.EvoScore>();

        static readonly Color Good = new Color(0.45f, 1f, 0.45f), Bad = new Color(1f, 0.45f, 0.40f), Dim = new Color(0.62f, 0.66f, 0.70f), White = Color.white;
        static readonly string[] LevelNamesEn = { "", "Fresh", "In-Training", "Rookie", "Champion", "Ultimate" };
        static readonly string[] LevelNamesZh = { "", "幼年期1", "幼年期2", "成長期", "成熟期", "完全體" };
        static string[] LevelNames { get { return Tr.Chinese ? LevelNamesZh : LevelNamesEn; } }
        // ours: the page in the current language (OPTIONS > LANGUAGE)
        static string Z(string en, string zh) { return Tr.Chinese ? zh : en; }
        static readonly int[] Hours = { 0, 6, 24, 72, 144, 0 };     // tickConditions: the evolution timer each level needs

        public void Open(int id) { _id = id; _page = 0; Showing = true; }
        public void Close() { Showing = false; }
        public void Turn(int side) { _page = (_page + side + Pages) % Pages; }

        static string Name(DiscImage disc, int id)
        {
            var d = id > 0 ? DigimonStatsCatalog.Get(disc, id) : null;
            return d != null && !string.IsNullOrEmpty(d.Name) ? d.Name : "?";
        }

        static int LevelOf(DiscImage disc, int id) { var d = id > 0 ? DigimonStatsCatalog.Get(disc, id) : null; return d != null ? d.Level : 0; }

        public void Draw(DiscImage disc, ScriptState s, PartnerCare care, System.Func<int, string> itemName)
        {
            if (!Showing || disc == null || s == null || care == null) return;
            UiKit.Box(new Rect(30, 30, 1355, 1000), new Color(0.08f, 0.12f, 0.16f, 0.97f));
            if (_page == 0) Targets(disc, s, care, itemName); else Rules(_page);
            UiKit.Text(new Rect(60, 985, 1300, 40), Z("Page ", "第 ") + (_page + 1) + " / " + Pages + Z("     LEFT / RIGHT: page     SQUARE or TRIANGLE: close", " 頁     左／右：換頁     方塊或三角：關閉"), 26, Dim);
        }

        // ---------------------------------------------------------------- page 1: this Digimon's evolutions against the partner's numbers now
        const int Size = 24;
        const float LabelX = 60, LabelW = 300, RowH = 32;
        float _y, _colW;

        // the evolution items (useItem / EvoItem): items 0x47-0x72 by EVOLUTION_ITEM_TARGET (0x80127C5C), 0x7D-0x7F always 0x40 / 0x3F / 0x41
        static string ItemsFor(PartnerCare care, int target, System.Func<int, string> itemName)
        {
            if (care.EvoItemTarget == null || itemName == null) return null;
            string all = null;
            for (int i = 0x47; i < 0x73; i++) if (care.EvoItemTarget(i) == target) all = (all == null ? "" : all + ", ") + itemName(i);
            int special = target == 0x40 ? 0x7D : target == 0x3F ? 0x7E : target == 0x41 ? 0x7F : -1;
            if (special > 0) all = (all == null ? "" : all + ", ") + itemName(special);
            return all;
        }

        void Label(string t, Color c) { UiKit.Text(new Rect(LabelX, _y, LabelW, RowH), t, Size, c); }

        // one table row: the label, then a cell per evolution; long cells wrap and make the row taller
        void Row(string label, Color labelColour, System.Func<int, string> text, System.Func<int, Color> colour)
        {
            float h = RowH;
            for (int k = 0; k < _log.Count; k++) h = Mathf.Max(h, UiKit.Height(text(k), Size, _colW - 8) + 4);
            Label(label, labelColour);
            for (int k = 0; k < _log.Count; k++)
                UiKit.Text(new Rect(LabelX + LabelW + _colW * k, _y, _colW - 8, h), text(k), Size, colour(k), TextAnchor.UpperCenter, true);
            _y += h;
        }

        void Line() { UiKit.Fill(new Rect(LabelX, _y - 3, 1295, 2), UiKit.Frame); }

        void Targets(DiscImage disc, ScriptState s, PartnerCare care, System.Func<int, string> itemName)
        {
            var p = s.Para;
            int level = LevelOf(disc, _id);
            bool mine = _id == care.PartnerType;
            UiKit.Text(new Rect(60, 42, 1300, 50), Z("EVOLUTION GUIDE   ", "進化指南   ") + Name(disc, _id) + Z("  (", "（") + LevelNames[Mathf.Clamp(level, 0, 5)] + Z(")", "）"), 40, UiKit.Yellow);
            string sub = mine ? Z("Your partner now.", "你現在的夥伴。") : Z("As if your partner were " + Name(disc, _id) + ", with your partner's numbers now.", "假設你的夥伴是" + Name(disc, _id) + "，用夥伴現在的數值計算。");
            if (level >= 1 && level <= 4)
                sub += mine ? Z("  Evolution timer: " + p.EvoTimer + " h of " + Hours[level] + " h (hours since it last evolved).", "  進化計時：" + p.EvoTimer + " / " + Hours[level] + " 小時（距離上次進化的時數）。")
                            : Z("  It needs " + Hours[level] + " h as " + LevelNames[level] + " first.", "  須先以" + LevelNames[level] + "度過 " + Hours[level] + " 小時。");
            string own = ItemsFor(care, _id, itemName);
            if (own != null)
            {
                bool on = level >= 2 && _id != 0x40 && _id != 0x3F && _id != 0x41;
                sub += Tr.Chinese ? "  變成" + Name(disc, _id) + "的道具：" + own + (on ? "（對" + LevelNames[level - 1] + "使用）。" : "。")
                                  : "  Item that makes " + Name(disc, _id) + ": " + own + (on ? " (used on " + (level == 2 ? "a" : "an") + " " + LevelNames[level - 1] + ")." : ".");
            }
            _y = 92;
            UiKit.Text(new Rect(60, _y, 1295, 200), sub, Size, White, TextAnchor.UpperLeft, true);
            _y += UiKit.Height(sub, Size, 1295) + 10;

            _log.Clear();
            int winner = care.Project(_id, _log);
            if (level == 1) { UiKit.Text(new Rect(60, _y + 20, 1295, 300), Z("A Fresh Digimon has one evolution: it becomes " + Name(disc, winner) + " after 6 hours, whatever its care.", "幼年期1的數碼寶貝只有一種進化：不論怎麼照顧，6 小時後都會變成" + Name(disc, winner) + "。"), 30, White, TextAnchor.UpperLeft, true); return; }
            if (level >= 5 || _log.Count == 0) { UiKit.Text(new Rect(60, _y + 20, 1295, 300), Z("An Ultimate does not evolve by its numbers. Only special evolutions (by item or event) reach it or leave it.", "完全體不會依數值進化。只有特殊進化（道具或事件）能進化成它或從它進化。"), 30, White, TextAnchor.UpperLeft, true); return; }

            // the table: one column per evolution, in the game's order (the order decides ties)
            _colW = Mathf.Min(165f, (1355f - LabelW - LabelX) / _log.Count);
            int[] mine6 = { s.Stats[4], s.Stats[5], s.Stats[0], s.Stats[1], s.Stats[2], s.Stats[3] };
            string[] statNames = Tr.Chinese ? new[] { "HP", "MP", "攻擊力", "防禦力", "速度", "智力" } : new[] { "HP", "MP", "Offense", "Defense", "Speed", "Brains" };
            string any = Z("any", "不限"), yes = Z("yes", "是"), no = Z("no", "否");
            System.Func<int, string> atMost = n => Z("at most " + n, n + " 以下"), atLeast = n => Z("at least " + n, n + " 以上");
            int mastered = care.MasteredMoves(), win = FindIndex(winner);
            System.Func<int, EvoRequirement> req = k => care.ReqOf(_log[k].Target);
            System.Func<bool, Color> ok = b => b ? Good : Bad;

            Row(Z("Evolution", "進化"), UiKit.Yellow, k => Name(disc, _log[k].Target), k => k == win ? UiKit.Yellow : White);
            Line();
            // point 1: care mistakes ("at least 0" is always met)
            Row(Z("Care mistakes: ", "照顧失誤：") + p.CareMistakes, UiKit.Yellow, k => (req(k).Flags & 0x10) != 0 ? atMost(req(k).Care) : req(k).Care <= 0 ? any : atLeast(req(k).Care), k => ok(_log[k].Care));
            // point 2: weight
            Row(Z("Weight: ", "體重：") + s.Weight + " G", UiKit.Yellow, k => (req(k).Weight - 5) + " - " + (req(k).Weight + 5), k => ok(_log[k].Weight));
            // point 3: stats (HP and MP are counted / 10, so a requirement of 100 is 1000 HP)
            int highest = 0;
            for (int i = 0; i < 6; i++) { bool topStat = true; for (int j = 0; j < 6; j++) if (Counted(mine6, i) < Counted(mine6, j)) topStat = false; if (topStat) highest = i; }
            for (int i = 0; i < 6; i++)
            {
                int stat = i;
                System.Func<int, int> need = k => { var r = req(k); return stat == 0 ? r.Hp : stat == 1 ? r.Mp : stat == 2 ? r.Off : stat == 3 ? r.Def : stat == 4 ? r.Speed : r.Brain; };
                // Rookie targets (calculateRequirementScore, level 3): a 1 marks a stat that earns the point when it is the partner's highest
                if (level == 2)
                    Row(statNames[i] + ": " + mine6[i] + (i == highest ? Z("  (highest)", "（最高）") : ""), i == highest ? UiKit.Yellow : White,
                        k => need(k) == 1 ? Z("if highest", "最高時") : "-", k => need(k) != 1 ? Dim : stat == highest ? Good : Dim);
                else
                    Row(statNames[i] + ": " + mine6[i], White, k => need(k) == -1 ? "-" : (stat < 2 ? need(k) * 10 : need(k)) + "+",
                        k => need(k) == -1 ? Dim : ok(mine6[stat] / (stat < 2 ? 10 : 1) >= need(k)));
            }
            Row(level == 2 ? Z("Stats point (highest)", "數值點（最高）") : Z("Stats point (all met)", "數值點（全部達到）"), UiKit.Yellow, k => _log[k].Stats ? yes : no, k => ok(_log[k].Stats));
            Line();

            // point 4: the bonus, any one of these. Values below 1 ("at least -2 battles", "0 techniques") are always met: shown as "any".
            BonusRow(Z("From Digimon", "進化前"), k => req(k).Digimon == -1 ? null : Name(disc, req(k).Digimon), k => _id == req(k).Digimon);
            BonusRow(Z("Discipline: ", "紀律：") + s.Discipline, k => req(k).Discipline == -1 ? null : req(k).Discipline + "+", k => s.Discipline >= req(k).Discipline);
            BonusRow(Z("Happiness: ", "快樂度：") + s.Happiness, k => req(k).Happiness == -1 ? null : req(k).Happiness + "+", k => s.Happiness >= req(k).Happiness);
            BonusRow(Z("Battles: ", "戰鬥次數：") + p.Battles, k => req(k).Battles == -1 ? null : (req(k).Flags & 1) != 0 ? atMost(req(k).Battles) : req(k).Battles <= 0 ? any : atLeast(req(k).Battles),
                     k => (req(k).Flags & 1) != 0 ? p.Battles <= req(k).Battles : p.Battles >= req(k).Battles);
            BonusRow(Z("Techniques: ", "招式數：") + mastered, k => req(k).Techs == -1 ? null : req(k).Techs <= 0 ? any : req(k).Techs + "+", k => mastered >= req(k).Techs);
            Row(Z("Bonus point (any 1)", "額外點（任一項）"), UiKit.Yellow, k => _log[k].Bonus ? yes : no, k => ok(_log[k].Bonus));
            Line();

            Row(Z("Raised before", "曾經培育"), White, k => (_log[k].Target < s.Raised.Length && s.Raised[_log[k].Target] ? yes : no) + (_log[k].RaisedRule < 0 ? Z(" (out)", "（出局）") : _log[k].RaisedRule > 0 ? " (+1)" : ""),
                k => _log[k].RaisedRule < 0 ? Bad : White);
            Row(Z("Points (3 needed)", "點數（需要 3）"), UiKit.Yellow, k => _log[k].Points.ToString(), k => ok(_log[k].Qualifies));
            if (level >= 3) Row(Z("Average of its stats", "所需數值平均"), UiKit.Yellow, k => _log[k].Average >= 0 ? _log[k].Average.ToString() : "-", k => k == win ? UiKit.Yellow : White);
            bool anyItem = false;
            for (int k = 0; k < _log.Count; k++) if (ItemsFor(care, _log[k].Target, itemName) != null) anyItem = true;
            if (anyItem) { Line(); Row(Z("Evolution item", "進化道具"), White, k => ItemsFor(care, _log[k].Target, itemName) ?? "-", k => ItemsFor(care, _log[k].Target, itemName) != null ? UiKit.Cyan : Dim); }

            string verdict;
            if (winner > 0 && win < 0) verdict = Z("Nothing qualifies, and the timer has reached 96 h: it would become " + Name(disc, winner) + ".", "沒有符合的進化，而計時已達 96 小時：會變成" + Name(disc, winner) + "。");   // getRookieEvolutionTarget's Numemon
            else if (winner > 0) verdict = Tr.Chinese ? (mine ? "以現在的數值會變成" : "以這些數值會變成") + Name(disc, winner) + "。" : (mine ? "With its numbers now it would become " : "With these numbers it would become ") + Name(disc, winner) + ".";
            else verdict = Z("Nothing qualifies with these numbers yet.", "以這些數值還沒有符合的進化。") + (level == 3 ? Z("  A Rookie with nothing to become turns into Numemon at 96 h.", "  沒有可進化對象的成長期，在 96 小時會變成鼻涕獸。") : "");
            if (mine && winner > 0 && p.EvoTimer < Hours[level]) verdict += Z("  (once the timer reaches " + Hours[level] + " h)", "（計時達到 " + Hours[level] + " 小時後）");
            UiKit.Text(new Rect(60, _y + 8, 1295, 80), verdict, 28, UiKit.Yellow, TextAnchor.UpperLeft, true);
        }

        static int Counted(int[] st, int i) { return i < 2 ? st[i] / 10 : st[i]; }

        int FindIndex(int target) { for (int k = 0; k < _log.Count; k++) if (_log[k].Target == target) return k; return -1; }

        void BonusRow(string name, System.Func<int, string> text, System.Func<int, bool> met)
        {
            bool any = false;
            for (int k = 0; k < _log.Count; k++) if (text(k) != null) any = true;
            if (!any) return;       // no evolution here asks for it
            Row(name, White, k => text(k) ?? "-", k => text(k) == null ? Dim : met(k) ? Good : Bad);
        }

        // ---------------------------------------------------------------- pages 2-3: the rules (main/evolution.c, partner_impl.c)
        static string[] RulesText { get { return Tr.Chinese ? RulesZh : RulesEn; } }

        static readonly string[] RulesZh =
        {
            "遊戲如何決定進化\n\n" +
            "時機：夥伴處於一般狀態（不在吃東西、睡覺、訓練、戰鬥或選單中）且沒有劇本在執行時，每一刻都會檢查。" +
            "需要進化計時，也就是距離上次進化的時數：幼年期1 6 小時、幼年期2 24 小時、成長期 72 小時、成熟期 144 小時。計時不是年齡。\n\n" +
            "每種可能的進化最多得 4 點，各 1 點，沒有先後之分：\n" +
            "1. 照顧失誤：至少或至多某個次數（依數碼寶貝而定）。\n" +
            "2. 體重：在該數碼寶貝體重的 ±5 以內。\n" +
            "3. 數值：成長期的話，夥伴最高的數值必須是該成長期列出的一項（第 1 頁的「最高時」）。成熟期或完全體的話，要求的每項數值都必須達到。HP 和 MP 以除以 10 計算（100 代表 1000）。\n" +
            "4. 額外：以下任一項：從某隻數碼寶貝進化、紀律、快樂度、戰鬥次數、招式數。符合多項也只得 1 點。" +
            "（每隻成長期的表都要求至少 -2 場戰鬥和 0 個招式，所以額外點一定會得到：顯示為「不限」。）\n" +
            "3 點以上就能被選上。",

            "多種進化都符合時\n\n" +
            "幼年期2到成長期：點數最多的勝出。點數相同：先列出的（圖表進化樹左邊）勝出。\n\n" +
            "成長期到成熟期、成熟期到完全體：點數只決定符不符合。在符合的進化中，所需數值平均最高的勝出。相同：先列出的勝出。\n" +
            "（原版遊戲在落選的候選之後沒有把總和歸零，結果算進了下一個的平均。本移植版已修正。）\n\n" +
            "曾經培育：與目前最佳者比較時，若目前最佳者從未培育過，曾經培育過的會失去點數；若目前最佳者培育過，從未培育過的會多得 1 點。\n\n" +
            "沒有符合的進化：成長期在計時達到 96 小時後會變成鼻涕獸；其他的會繼續等待並再次檢查。\n\n" +
            "特殊進化不經過這些：有些在睡覺或稱讚／責罵後依機率發生，病毒量表到 16（大便獸），以及進化道具。",
        };

        static readonly string[] RulesEn =
        {
            "HOW THE GAME CHOOSES AN EVOLUTION\n\n" +
            "When: the check runs every moment the partner is in its normal state (not eating, sleeping, training, fighting or in a menu) and no script runs. " +
            "It needs the evolution timer, the hours since the last evolution: Fresh 6 h, In-Training 24 h, Rookie 72 h, Champion 144 h. The timer is not the age.\n\n" +
            "Each possible evolution earns up to 4 points, one each, in no order of importance:\n" +
            "1. Care mistakes: at least the number, or at most it (it depends on the Digimon).\n" +
            "2. Weight: within 5 of the Digimon's weight.\n" +
            "3. Stats: for a Rookie, the partner's highest stat must be one the Rookie lists (\"if highest\" on page 1). For a Champion or Ultimate, every stat it asks for must be reached. HP and MP count divided by 10 (100 means 1000).\n" +
            "4. Bonus: any one of: coming from a certain Digimon, discipline, happiness, battles, number of techniques. Several still give 1 point. " +
            "(Every Rookie's table asks for at least -2 battles and 0 techniques, so its bonus point is always earned: shown as \"any\".)\n" +
            "3 points or more and it can be chosen.",

            "WHEN SEVERAL QUALIFY\n\n" +
            "In-Training to Rookie: the most points wins. Equal points: the one listed first (left in the Chart's Tree) wins.\n\n" +
            "Rookie to Champion, Champion to Ultimate: points only decide yes or no. Among those that qualify, the one with the highest average of the stats it asks for wins. Equal: the one listed first.\n" +
            "(The original game did not clear the sum after a losing candidate, so it spilled into the next one's average. Fixed in this port.)\n\n" +
            "Raised before: when one is compared with the best so far, a Digimon you raised before loses its points if the best so far was never raised, and a never-raised one gets 1 point more if the best so far was raised.\n\n" +
            "Nothing qualifies: a Rookie becomes Numemon once its timer reaches 96 h; others wait and are checked again.\n\n" +
            "Special evolutions skip all this: some after sleeping or Praise / Scold with a random chance, the virus bar at 16 (Sukamon), evolution items.",
        };

        void Rules(int page)
        {
            UiKit.Text(new Rect(60, 50, 1295, 930), RulesText[page - 1], 29, White, TextAnchor.UpperLeft, true);
        }
    }
}
