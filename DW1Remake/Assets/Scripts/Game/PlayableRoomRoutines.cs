using System.Collections;
using System.Collections.Generic;
using DW1Remake.Formats;
using UnityEngine;

namespace DW1Remake.Game
{
    /// <summary>
    /// Script built-ins that talk through script 0's text tables (showMapHeadTextbox / showMapheadSelection, shop/script_menu.c):
    /// 0x0A Birdramon transport (tickBirdraTransport). The texts come from DG.SCN entry 0; the tables from the exe.
    /// </summary>
    public sealed partial class PlayableRoom
    {
        bool _routineOn;

        // colours: keep the text's colour codes (for the dialogue box, which draws them with the game font)
        string HeadText(int section, int idx, bool colours = false) { return ScriptRunner.MapHeadText(_scripts, _state, section, idx, _state.Vars[0xFE], colours) ?? ""; }

        List<ItemEntry> _itemCatalog;
        ItemMenuView _itemBox;

        // openDiscardItem (built-in 0, ITEM_MENU_TYPE 5) with tickItemMenu and tickSingleCardShop. Picked: variable 254 = the item, flag 3 off.
        // Circle: only while flag 3 is on (the script sets it first): 254 = 0xFF, flag 3 off. Nothing that can be handed over: 254 = 0xFF, flag 3 on.
        // Not ported: R1 + up/down (to the first / last row) and Start (the item's description box).
        IEnumerator DiscardItem()
        {
            _routineOn = true;
            var v = ItemBox();
            if (v != null) v.Type = 5;
            if (v == null || !v.Fill(_state))
            {
                _state.SetFlag(3, true); _state.Vars[0xFE] = 0xFF;
                _routineOn = false; AdvanceScript(); yield break;
            }
            // setupBoxOrigin(0xFD): the box grows out of the 10 x 10 square on the boy's bone 1
            v.Open(v.List, new RectInt(-0x24, -0x62, 0xA6, 0x81), DialogueOrigin(0xFD) ?? new RectInt(0, 0, 10, 10), 0xF1);
            bool done = false;
            while (!done)
            {
                yield return null;
                if (v.Confirm.State == 1)
                {
                    if (GameMenus.CancelKey()) { v.Close(v.Confirm); Snd(4); continue; }
                    int side = GameMenus.SideStep();
                    if (side < 0) { v.Pick = 0; Snd(2); continue; }
                    if (side > 0) { v.Pick = 1; Snd(2); continue; }
                    if (ActionPressed())
                    {
                        v.Close(v.Confirm);
                        if (v.Pick == 0) { _state.Vars[0xFE] = (byte)v.Types[v.Row]; _state.SetFlag(3, false); Snd(3); done = true; }
                        else Snd(4);
                    }
                    continue;
                }
                if (v.Confirm.State != 0 || v.List.State != 1) continue;
                if (ActionPressed()) { v.Ask(); continue; }
                if (GameMenus.CancelKey())
                {
                    if (_state.GetFlag(3)) { _state.Vars[0xFE] = 0xFF; _state.SetFlag(3, false); done = true; }
                    Snd(4);
                    continue;
                }
                int step = GameMenus.RepeatStep();
                if (step < 0) v.Up(); else if (step > 0) v.Down();
            }
            v.Close(v.List);
            _routineOn = false;
            AdvanceScript();        // the script goes on while the box shrinks away
        }

        ItemMenuView ItemBox()
        {
            if (_itemBox == null && _itemCatalog != null)
            {
                _itemBox = ItemMenuView.Load(_disc, Battle.StatFont, _menus != null ? _menus.Icons : null, _itemCatalog, HeadText(0x4D8, 0));
                if (_itemBox != null) { _itemBox.Sound = Snd; _itemBox.DigimonName = t => _state.SpeciesNameOf != null ? _state.SpeciesNameOf(t) : ""; _itemBox.CardHeld = c => _state.CardCount[c]; }
            }
            return _itemBox;
        }

        // ------------------------------------------------------------------ 0x08 shop

        // SHOPKEEP_DIGIMON_TYPES (11 bytes), BABY_IDS (8), the one map with a sound after each sale (MAIN_D_801345C0 map, 801345C2 bank and sound)
        const uint ShopkeepTypes = 0x8012FE5C, BabyIds = 0x801345B4, ShopSoundMap = 0x801345C0;
        byte[] _exeBytes;
        byte[] ExeBytes { get { return _exeBytes ?? (_exeBytes = _disc.ReadFile("/SLUS_010.32")); } }
        int ExeAt(uint a) { return (int)(a - System.BitConverter.ToUInt32(ExeBytes, 0x18) + 0x800); }

        // getShopkeeperLine: variable 254 = 0xFF: section 0x4CE; else the shopkeeper's Digimon type in SHOPKEEP_DIGIMON_TYPES gives 0x4C4 + its place (0x4C4 if none)
        int ShopSection(int npc)
        {
            if (npc == 0xFF) return 0x4CE;
            foreach (var n in _npcs)
                if (n.Data.Script == npc && n.T != null)
                {
                    for (int i = 0; i < 11; i++) if (ExeBytes[ExeAt(ShopkeepTypes) + i] == n.Data.Type) return 0x4C4 + i;
                    break;
                }
            return 0x4C4;
        }

        // isPartnerBaby: the partner's type is one of BABY_IDS
        bool PartnerIsBaby()
        {
            for (int i = 0; i < 8; i++) if (ExeBytes[ExeAt(BabyIds) + i] == PartnerType) return true;
            return false;
        }

        // playShopSoundOnlyInSavannah
        void ShopSaleSound()
        {
            int at = ExeAt(ShopSoundMap);
            if (_mapId == System.BitConverter.ToUInt16(ExeBytes, at) && _audio != null) _audio.Play(ExeBytes[at + 2], ExeBytes[at + 3]);
        }

        // the shopkeeper's Bits line (text 6: "Bits" and MONEY in 6 places with the game's padding)
        string ShopBitsText(int section)
        {
            _state.GameDigits = true;
            string t = HeadText(section, 6);
            _state.GameDigits = false;
            return t;
        }

        // openShop (built-in 8, states 0-9) with tickItemMenu, tickItemMenuAmountBox and tickItemMenuDescriptionBox. Flag 0x180 + item = sold here
        // (stock); variable 254 = the shopkeeper. Up / down repeat while held (setInputRepeatMask 0x5000, GameMenus.RepeatStep).
        IEnumerator Shop(System.Func<int, bool> stock, int npc)
        {
            _routineOn = true;
            var v = ItemBox();
            int sec = ShopSection(npc);
            bool any = false, dealt = false;
            for (int i = 0; i < 0x80; i++) if (stock(i)) { any = true; break; }
            if (v == null || !any)
            {
                yield return HeadSay(sec, 1, npc);              // "I don't have anything to sell."
                ShopEnd(); yield break;
            }
            yield return HeadSay(sec, 0, npc);                  // "What are you going to do?"
            int choice = 0;
            while (true)
            {
                // state 3: the Bits box, then the boy's choice Buy / Sell / See ya (showShopkeepSelection(2, 0xFD); the cursor stays where it was)
                v.OpenBits(DialogueOrigin(0xFD), ShopBitsText(sec));
                int pick = 0;
                yield return HeadAsk(sec, 2, 3, p => pick = p, 0xFD, choice);
                choice = pick;
                if (pick == 2) break;
                // states 4 / 5: the list grows out of the shopkeeper (buying) or the boy (selling); the shopkeeper's line stays while it is up
                v.Type = pick;
                if (pick == 0) v.FillBuy(_state, stock); else v.Fill(_state);
                RectInt? from = DialogueOrigin(pick == 0 ? npc : 0xFD);
                v.Open(v.List, v.ListRect, from ?? new RectInt(-5, -5, 10, 10), 0xF1);
                _line = HeadText(sec, pick == 0 ? 8 : 9, true); _speaker = npc; _choiceOpts = null;
                int next = 0;
                yield return ShopList(v, sec, npc, r => next = r);
                // state 7 (a sale: "Thanks!", or the baby discount line) / 8 ("Have anything else?"); then the choice again
                v.Close(v.List);
                _line = null;
                if (next == 7) { dealt = true; yield return HeadSay(sec, v.Type == 0 && PartnerIsBaby() ? 0x0D : 0x0A, npc); }
                else yield return HeadSay(sec, 0x0B, npc);
            }
            // state 6: the Bits box shuts; "Come again." after a sale, "That's too bad." without one
            v.Close(v.Bits);
            yield return HeadSay(sec, dealt ? 4 : 5, npc);
            ShopEnd();
        }

        // tickItemMenu with its amount and description boxes, until a sale (7) or Triangle (8): the loop the shop and the recycle shop share
        IEnumerator ShopList(ItemMenuView v, int sec, int npc, System.Action<int> result)
        {
            int next = 0;
            while (next == 0)
            {
                yield return null;
                if (v.Desc.State != 0)
                {
                    // tickItemMenuDescriptionBox: Cross, Triangle or Start closes it
                    if (v.Desc.State == 1 && (ShopCross() || GameMenus.TriangleOnly() || GameMenus.StartKey())) { v.Close(v.Desc); Snd(3); }
                    continue;
                }
                if (v.Amount.State != 0) { if (v.Amount.State == 1) next = TickShopAmount(v, sec, npc); continue; }
                if (v.List.State != 1) continue;
                // tickItemMenu: Cross the amount box, Triangle back (state 8), up / down (R1: to the first / last row), Start the description
                if (GameMenus.StartKey()) { if (v.OpenDesc()) Snd(3); continue; }
                if (ShopCross()) { int r = v.Row; v.OpenAmount(_state.Money, r < v.Types.Count && v.Types[r] != 0xFF ? _state.Bag[v.Types[r]] : 0); continue; }
                if (GameMenus.TriangleOnly()) { next = 8; Snd(4); continue; }
                int step = GameMenus.RepeatStep();
                if (step < 0) { if (GameMenus.WalkHeld()) v.ToTop(); else v.Up(); }
                else if (step > 0) { if (GameMenus.WalkHeld()) v.ToBottom(); else v.Down(); }
            }
            result(next);
        }

        // openRecycleShop (built-in 8 with flag 3 on, states 0-9): what the thieves took (handleItemLoss) sold back at full price. Variable 254 = the
        // keeper. Only "Buy / See ya": state 3 asks (text 3), 4 the list (type 2, text 8), 7 after a sale (text 10, or 0x0D for a baby), 8 Triangle
        // (text 0x0B), 9 sold out (0x0C), 5 / 6 the end (text 4 after a sale, else 5).
        IEnumerator RecycleShop(int npc)
        {
            _routineOn = true;
            var v = ItemBox();
            int sec = ShopSection(npc);
            bool any = v != null && v.FillRecycle(_state), dealt = false;
            if (!any)
            {
                yield return HeadSay(sec, 1, npc);              // "I don't have anything to sell."
                ShopEnd(); yield break;
            }
            yield return HeadSay(sec, 0, npc);
            int choice = 0;
            while (true)
            {
                v.OpenBits(DialogueOrigin(0xFD), ShopBitsText(sec));
                int pick = 0;
                yield return HeadAsk(sec, 3, 2, p => pick = p, 0xFD, choice);
                choice = pick;
                if (pick == 1) break;
                v.Type = 2;
                v.FillRecycle(_state);
                RectInt? from = DialogueOrigin(npc);
                v.Open(v.List, v.ListRect, from ?? new RectInt(-5, -5, 10, 10), 0xF1);
                _line = HeadText(sec, 8, true); _speaker = npc; _choiceOpts = null;
                int next = 0;
                yield return ShopList(v, sec, npc, r => next = r);
                v.Close(v.List);
                _line = null;
                if (next == 8) { yield return HeadSay(sec, 0x0B, npc); continue; }
                dealt = true;
                yield return HeadSay(sec, PartnerIsBaby() ? 0x0D : 0x0A, npc);
                if (!v.FillRecycle(_state)) { yield return HeadSay(sec, 0x0C, npc); break; }     // state 9: nothing left to buy
            }
            v.Close(v.Bits);
            yield return HeadSay(sec, dealt ? 4 : 5, npc);
            ShopEnd();
        }

        // ---- the card shop (built-in 0x0E; script_menu.c rollCardPack, openBuyCardMenu, openSellCardMenu; texts DG0 0x4D3). Variable 254 = the keeper.
        // Flags 3 and 4 both off: a pack of three cards. 3 off, 4 on: buy one of the six cards of the day.
        // 3 on: sell cards (half the price, any number).
        IEnumerator CardShop(int npc)
        {
            _routineOn = true;
            const int Sec = 0x4D3;
            var v = ItemBox();
            if (v == null) { ShopEnd(); yield break; }
            if (!_state.GetFlag(3) && !_state.GetFlag(4))
            {
                // rollCardPack: three cards, each with its line (new: by rarity; held: 5, or 6 from six held) and a Cross after it
                for (int k = 0; k < 3; k++) yield return HeadSay(Sec, TakeCard(v, RollCard(v)), 0xFD);
                ShopEnd(); yield break;
            }
            int keeper = npc;
            if (!_state.GetFlag(3))
            {
                // openBuyCardMenu: nothing to buy = text 1 and out; else the list (type 3) grows out of the keeper, text 0 stays up
                v.Type = 3;
                if (!v.FillCardOffers(_state)) { yield return HeadSay(Sec, 1, keeper); ShopEnd(); yield break; }
            }
            else
            {
                v.Type = 4;
                v.FillCardSell(_state);
            }
            bool buying = v.Type == 3;
            v.Open(v.List, v.ListRect, DialogueOrigin(buying ? keeper : 0xFD) ?? new RectInt(-5, -5, 10, 10), 0xF1);
            _line = HeadText(Sec, buying ? 0 : 8, true); _speaker = keeper; _choiceOpts = null;
            if (buying)
            {
                bool done = false;
                while (!done)
                {
                    yield return null;
                    if (v.Confirm.State == 1)
                    {
                        // tickSingleCardShop: left Yes, right No, Cross, Triangle shuts it
                        if (GameMenus.CancelKey()) { v.Close(v.Confirm); Snd(4); continue; }
                        int side = GameMenus.SideStep();
                        if (side < 0) { v.Pick = 0; Snd(2); continue; }
                        if (side > 0) { v.Pick = 1; Snd(2); continue; }
                        if (ShopCross())
                        {
                            v.Close(v.Confirm);
                            if (v.Pick == 0)
                            {
                                int card = v.Types[v.Row];
                                _state.CardCount[card]++; _state.Cards[card] = true;
                                _state.Money -= v.CardPrice(card);
                                _state.CardOffers[v.Cursor] = 0xFF;          // smth[box->cursor] = 0xFF
                                v.BitsText = ShopBitsText(ShopSection(keeper));
                                ShopSaleSound();
                                done = true;
                            }
                            else Snd(4);
                        }
                        continue;
                    }
                    if (v.Confirm.State != 0 || v.List.State != 1) continue;
                    if (ShopCross()) { v.Ask(); continue; }
                    if (GameMenus.TriangleOnly()) { Snd(4); done = true; continue; }
                    int step = GameMenus.RepeatStep();
                    if (step < 0) { if (GameMenus.WalkHeld()) v.ToTop(); else v.Up(); }
                    else if (step > 0) { if (GameMenus.WalkHeld()) v.ToBottom(); else v.Down(); }
                }
            }
            else
            {
                int next = 0;
                yield return ShopList(v, Sec, keeper, r => next = r);
                v.BitsText = ShopBitsText(ShopSection(keeper));
            }
            v.Close(v.List);
            _line = null;
            ShopEnd();
        }

        // ---- the merit shop (built-in 0x01; script_menu.c openMeritShop, texts DG0 0x4D4). Variable 254 = the keeper (ShogunGekomon, GKYO02).
        // State 0: text 0. State 3: the Bits box with the points (createShopBitsBox(0): shopkeeper text 7), then the boy's choice (text 1):
        // 4 a card (type 6, ITEM_MENU_LEFT, text 5), 5 an item (type 7, ITEM_MENU_RIGHT, text 6), 6 the end (the Bits box shuts, no text).
        // A card: createMeritCardTradeDialogue (SHOP_VARIABLE = its merit, text 7 + its rarity), then text 2: 8 gives it (MERIT += it, at most
        // 9999; one card fewer), 9 keeps it. An item: readSelectedItemMerit (SHOP_VARIABLE = its merit), text 3 with the list still up: 0x0C
        // (MERIT -= it, the item into the bag), 0x0D not. Each ending (and Triangle, 0x0A) shuts the list and goes back to state 3.
        IEnumerator MeritShop()
        {
            _routineOn = true;
            const int Sec = 0x4D4;
            int owner = _state.Vars[0xFE];
            var v = ItemBox();
            if (v == null) { ShopEnd(); yield break; }
            int bsec = ShopSection(owner);
            yield return HeadSay(Sec, 0, owner);
            while (true)
            {
                v.OpenBits(DialogueOrigin(0xFD), MeritBitsText(bsec));
                int pick = 0;
                yield return HeadAsk(Sec, 1, 3, p => pick = p, 0xFD);
                if (pick == 2) break;
                // states 4 / 5: the list grows out of the keeper (createCardMenu / createItemMenu with ITEM_MENU_TYPE 6 / 7); its line stays up
                v.Type = pick == 0 ? 6 : 7;
                if (pick == 0) v.FillCardSell(_state); else v.FillMeritItems(_state);
                v.Open(v.List, v.ListRect, DialogueOrigin(owner) ?? new RectInt(-5, -5, 10, 10), 0xF1);
                _line = HeadText(Sec, pick == 0 ? 5 : 6, true); _speaker = owner; _choiceOpts = null;
                int chosen = -1;
                while (true)
                {
                    yield return null;
                    if (v.Desc.State != 0)
                    {
                        // tickItemMenuDescriptionBox (items only): Cross, Triangle or Start closes it
                        if (v.Desc.State == 1 && (ShopCross() || GameMenus.TriangleOnly() || GameMenus.StartKey())) { v.Close(v.Desc); Snd(3); }
                        continue;
                    }
                    if (v.List.State != 1) continue;
                    if (pick == 1 && GameMenus.StartKey()) { if (v.OpenDesc()) Snd(3); continue; }
                    if (ShopCross())
                    {
                        // createMeritCardTradeDialogue / readSelectedItemMerit: only a row held (cards) or within reach (items)
                        int r = v.Row;
                        if (r < v.Types.Count && v.Types[r] != 0xFF && v.Amounts[r] != 0) { chosen = v.Types[r]; Snd(3); break; }
                        Snd(0xB); continue;
                    }
                    if (GameMenus.TriangleOnly()) { Snd(4); break; }            // state 0x0A
                    int step = GameMenus.RepeatStep();
                    if (step < 0) { if (GameMenus.WalkHeld()) v.ToTop(); else v.Up(); }
                    else if (step > 0) { if (GameMenus.WalkHeld()) v.ToBottom(); else v.Down(); }
                }
                if (chosen >= 0)
                {
                    int yes = 1;
                    if (pick == 0)
                    {
                        _state.ShopVariable = v.CardMerit(chosen);
                        yield return HeadSay(Sec, 7 + v.CardSprite(chosen), owner);
                        yield return HeadAsk(Sec, 2, 2, p => yes = p, 0xFD);
                        if (yes == 0)
                        {
                            v.Close(v.List);
                            _state.Stats[14] = (ushort)Mathf.Min(_state.Stats[14] + _state.ShopVariable, 9999);
                            int left = _state.CardCount[chosen] - 1;
                            _state.CardCount[chosen] = (byte)Mathf.Max(0, left); _state.Cards[chosen] = left > 0;
                            ShopSaleSound();
                        }
                    }
                    else
                    {
                        _state.ShopVariable = _itemCatalog[chosen].Merit;
                        yield return HeadAsk(Sec, 3, 2, p => yes = p, 0xFD);
                        if (yes == 0)
                        {
                            v.Close(v.List);
                            _state.Stats[14] = (ushort)Mathf.Max(0, _state.Stats[14] - _state.ShopVariable);
                            _state.AddItem(chosen, 1);
                            _state.SyncSlots();
                            ShopSaleSound();
                        }
                    }
                }
                v.Close(v.List);
                _line = null;
            }
            v.Close(v.Bits);
            ShopEnd();
        }

        // the keeper's points line (shopkeeper text 7: "Points" and MERIT in 4 places with the game's padding)
        string MeritBitsText(int section)
        {
            _state.GameDigits = true;
            string t = HeadText(section, 7);
            _state.GameDigits = false;
            return t;
        }

        // rollCard: a rarity (1% 0, 4% 1, 15% 2, 30% 3, 50% 4), then one of the cards of that rarity
        int RollCard(ItemMenuView v)
        {
            int r = Random.Range(0, 100), rarity = r == 0 ? 0 : r < 5 ? 1 : r < 0x14 ? 2 : r < 0x32 ? 3 : 4;
            var pool = new List<int>();
            for (int i = 0; i < 0x42; i++) if (v.CardSprite(i) == rarity) pool.Add(i);
            return pool[Random.Range(0, pool.Count)];
        }

        // showCardTextbox: the card goes into the collection (up to 9 of it) and its line is chosen: new = by rarity (2 / 3 / 4), held = 5, six or more = 6;
        // variable 249 = the card's Digimon for the text
        int TakeCard(ItemMenuView v, int card)
        {
            int n = _state.CardCount[card], sp = v.CardSprite(card);
            int line = n == 0 ? (sp == 0 ? 2 : sp == 1 ? 3 : 4) : n < 6 ? 5 : 6;
            if (n < 9) { _state.CardCount[card]++; _state.Cards[card] = true; }
            _state.Vars[249] = (byte)v.CardDigimonOf(card);
            return line;
        }

        // scriptCheckTournamentMedal (built-in 0x16; tournament.c): one medal at a time, while one is earned and not yet held: the six cups of a group, 100
        // tournament wins, or every card with the game beaten once. The text is DG0 0x4DB, line = the medal's number in the check.
        IEnumerator CheckMedals()
        {
            _routineOn = true;
            int[] ids = { 0, 1, 2, 3, 4, 0x0C };
            while (true)
            {
                int result = MedalEarned();
                if (result < 0 || _state.GetFlag(0x16C + ids[result])) break;
                _state.SetFlag(0x16C + ids[result], true);          // unlockMedal: trigger 0x16C + medal
                yield return HeadSay(0x4DB, result, 0xFF);
            }
            ShopEnd();
        }

        int MedalEarned()
        {
            bool all = true;
            for (int i = 0; i < 6; i++) if (!_state.GetFlag(15 + i)) { all = false; break; }
            if (all) return 0;
            all = true; for (int i = 0; i < 5; i++) if (!_state.GetFlag(21 + i)) { all = false; break; }
            if (all) return 1;
            all = true; for (int i = 0; i < 7; i++) if (!_state.GetFlag(26 + i)) { all = false; break; }
            if (all) return 2;
            all = true; for (int i = 0; i < 4; i++) if (!_state.GetFlag(33 + i)) { all = false; break; }
            if (all) return 3;
            if (_state.Stats[18] >= 100) return 4;
            for (int i = 0; i < 0x42; i++) if (_state.CardCount[i] == 0) return -1;
            return _state.GetFlag(50) ? 5 : -1;
        }

        void ShopEnd()
        {
            _routineOn = false;
            if (_runner != null) AdvanceScript();
        }

        // Cross alone (our ActionPressed also takes Enter = Start and Z = Circle, which the shop uses for other things)
        static bool ShopCross() { return ActionPressed() && !GameMenus.StartKey() && !GameMenus.CircleKey(); }

        // tickItemMenuAmountBox: up / down one (Circle held: ten, never past the most or below 1), Triangle back, Square the most, Cross buys or sells.
        // Returns 7 after a sale, else 0.
        int TickShopAmount(ItemMenuView v, int sec, int npc)
        {
            int step = GameMenus.RepeatStep();
            bool alt = GameMenus.CircleHeld();
            if (v.ShopAmount != v.MaxAmount && step < 0)
            {
                if (alt) { v.ShopAmount += 10; if (v.ShopAmount > v.MaxAmount) v.ShopAmount -= 10; } else v.ShopAmount++;
                Snd(2); return 0;
            }
            if (v.ShopAmount != 1 && step > 0)
            {
                if (alt) { int q = v.ShopAmount - 10; if (q <= 0) q += 10; v.ShopAmount = q; } else v.ShopAmount--;
                Snd(2); return 0;
            }
            if (GameMenus.TriangleOnly()) { v.Close(v.Amount); Snd(4); return 0; }
            if (GameMenus.SquareKey()) { v.ShopAmount = v.MaxAmount; Snd(3); return 0; }
            if (!ShopCross()) return 0;
            int owner;
            if (v.Type == 1)
            {
                _state.Money = Mathf.Min(_state.Money + v.ShopAmount * v.ShopPrice, 999999);
                _state.RemoveItem(v.ShopItem, v.ShopAmount);
                owner = npc;
            }
            else if (v.Type == 4)
            {
                // a card sold (tickItemMenuAmountBox, ITEM_MENU_TYPE >= 3): half the price each; the Bits stop at 999999
                _state.Money = Mathf.Min(_state.Money + v.ShopAmount * v.ShopPrice, 999999);
                int left = _state.CardCount[v.ShopItem] - v.ShopAmount;
                _state.CardCount[v.ShopItem] = (byte)Mathf.Max(0, left); _state.Cards[v.ShopItem] = left > 0;
                owner = npc;
            }
            else
            {
                int unit = v.ShopPrice;
                if (PartnerIsBaby()) { unit = unit * 90 / 100; _state.ShopVariable = (short)(v.ShopAmount * (v.ShopPrice - unit)); }
                _state.Money -= v.ShopAmount * unit;
                _state.AddItem(v.ShopItem, v.ShopAmount);
                if (v.Type == 2) { int rid = _state.RecycleId(v.ShopItem); if (rid != 0xFF) _state.Recycle[rid] = (byte)System.Math.Max(0, _state.Recycle[rid] - v.ShopAmount); }     // smth[getRecycleId + 6] -= SHOP_AMOUNT
                owner = 0xFD;
            }
            _state.SyncSlots();
            // closeTextbox(3, origin): the box shrinks into whoever gets the item (buying: the boy) or the Bits (selling: the shopkeeper)
            v.CloseAmountInto(DialogueOrigin(owner));
            v.BitsText = ShopBitsText(sec);
            ShopSaleSound();
            return 7;
        }

        // a world point in PS1 screen pixels from the middle of the picture
        Vector2 Ps1Point(Vector3 world)
        {
            if (_cam == null) return Vector2.zero;
            Vector3 sp = _cam.WorldToScreenPoint(world); Rect pic = UiKit.Picture;
            return new Vector2((sp.x - pic.x) / pic.width * 320f - 160f, (Screen.height - sp.y - pic.y) / pic.height * 240f - 120f);
        }

        // showMapHeadTextbox with SCRIPT_TEXTBOX_MODE 1: the owner (variable 254) says it; Cross goes on
        IEnumerator HeadSay(int section, int idx, int speaker = -1)
        {
            _line = HeadText(section, idx, true); _speaker = speaker < 0 ? _state.Vars[0xFE] : speaker; _choiceOpts = null;
            yield return null;
            while (!(DialogueReady && ActionPressed())) yield return null;
            _line = null;
            yield return null;
        }

        // showMapheadSelection with SCRIPT_TEXTBOX_MODE 2: the text's last 'count' lines are the choices; the state goes to next + choice
        IEnumerator HeadAsk(int section, int idx, int count, System.Action<int> answer, int speaker = -1, int start = 0)
        {
            var lines = new List<string>(HeadText(section, idx, true).Split('\n'));
            int q = Mathf.Max(0, lines.Count - count);
            _line = string.Join("\n", lines.GetRange(0, q).ToArray());
            _choiceOpts = lines.GetRange(q, lines.Count - q); _speaker = speaker < 0 ? _state.Vars[0xFE] : speaker;
            _choiceSel = Mathf.Clamp(start, 0, Mathf.Max(0, _choiceOpts.Count - 1));
            yield return null;
            while (true)
            {
                int ms = MenuStep();
                if (ms != 0) _choiceSel = (_choiceSel + ms + _choiceOpts.Count) % _choiceOpts.Count;
                if (ActionPressed()) break;
                yield return null;
            }
            int pick = _choiceSel;
            _line = null; _choiceOpts = null;
            answer(pick);
            yield return null;
        }

        // ------------------------------------------------------------------ 0x0B Item Keeper

        KeeperView _keeper;

        // tickItemKeeper (states 0-6) with tickItemKeeperWindow and tickItemMenuDescriptionBox.
        IEnumerator ItemKeeper()
        {
            _routineOn = true;
            int owner = _state.Vars[0xFE];
            yield return HeadSay(0x4D5, 0, owner);                 // "Do you have any business at the Item Keeper?"
            int pick = 0;
            yield return HeadAsk(0x4D5, 1, 2, a => pick = a, 0xFD);     // the boy answers: Yes / No
            if (_keeper == null && _itemCatalog != null) { _keeper = KeeperView.Load(_disc, Battle.StatFont, _itemCatalog); if (_keeper != null) _keeper.Sound = Snd; }
            var k = _keeper;
            if (pick == 0 && k != null)
            {
                // state 4: the lists, the window out of the keeper and the boy, the help line stays up meanwhile
                k.Side = 0;
                k.Fill(_state);
                k.OpenWindow(DialogueOrigin(owner) ?? new RectInt(-5, -5, 10, 10), DialogueOrigin(0xFD) ?? new RectInt(-5, -5, 10, 10));
                _line = HeadText(0x4D5, 3, true); _speaker = owner; _choiceOpts = null;
                while (true)
                {
                    yield return null;
                    if (k.Desc.State != 0)
                    {
                        if (k.Desc.State == 1 && (ShopCross() || GameMenus.TriangleOnly() || GameMenus.StartKey())) { k.Close(k.Desc); Snd(3); }
                        continue;
                    }
                    if (k.Left.State != 1 || k.Right.State != 1) continue;
                    if (GameMenus.TriangleOnly()) { Snd(4); break; }
                    int side = GameMenus.SideStep();
                    if (side < 0) { k.Side = 0; Snd(2); continue; }
                    if (side > 0) { k.Side = 1; Snd(2); continue; }
                    if (k.ActiveCount == 0) continue;
                    if (GameMenus.StartKey()) { k.OpenDesc(); Snd(3); continue; }
                    // setInputRepeatMask(0x5000 | CONFIRM | ALT): Cross and Circle repeat while held, as up / down do
                    if (GameMenus.CrossRepeat && !GameMenus.StartKey()) { k.Move(_state, 1); Snd(3); continue; }
                    if (GameMenus.CircleRepeat) { k.Move(_state, 10); Snd(3); continue; }
                    if (GameMenus.SquareKey()) { k.Move(_state, 0); Snd(3); continue; }
                    int ms = GameMenus.RepeatStep();
                    if (ms < 0) { if (GameMenus.WalkHeld()) k.ToTop(); else k.Up(); }
                    else if (ms > 0) { if (GameMenus.WalkHeld()) k.ToBottom(); else k.Down(); }
                }
                // state 6: both boxes shut
                k.CloseWindow();
                _line = null;
            }
            yield return HeadSay(0x4D5, 2, owner);                 // "Come to this shop when you need it."
            _routineOn = false;
            AdvanceScript();
        }

        // ------------------------------------------------------------------ 0x0A Birdramon transport

        // BIRDRA_TRANSPORT_TARGETS (exe 0x8013024C, 6 x 8 bytes): map, exit, flag that opens it, cost in Bits
        const uint BirdraTargets = 0x8013024C, MapNamePtr = 0x801291BC, MapEntries = 0x801292D4;
        ScriptListView _scriptList;

        ScriptListView ScriptList()
        {
            if (_scriptList == null) { _scriptList = ScriptListView.Load(_disc, Battle.StatFont); if (_scriptList != null) _scriptList.Sound = Snd; }
            return _scriptList;
        }

        // tickBirdraTransport (states 0-6) with birdraFillTargetList, createBirdraTransportMenu and tickBirdraTransportMenu
        IEnumerator BirdraTransport()
        {
            _routineOn = true;
            byte[] exe = ExeBytes;
            int owner = _state.Vars[0xFE];
            // birdraFillTargetList: the destinations whose flag is on; those the Bits pay for get bit 0x80 (can be picked)
            var rows = new List<int>();
            var v = ScriptList();
            if (v != null) { v.Rows.Clear(); v.Ok.Clear(); }
            for (int i = 0; i < 6; i++)
            {
                int o = ExeAt(BirdraTargets) + 8 * i;
                int map = exe[o], flag = System.BitConverter.ToUInt16(exe, o + 2), cost = System.BitConverter.ToInt32(exe, o + 4);
                if (!_state.GetFlag(flag)) continue;
                int name = exe[ExeAt(MapEntries) + 16 * map + 15];         // MAP_ENTRIES[map].loadingName -> MAP_NAME_PTR
                uint ptr = System.BitConverter.ToUInt32(exe, ExeAt(MapNamePtr) + 4 * name);
                rows.Add(i);
                if (v != null) { v.Rows.Add(ScriptListView.BirdraRow(Exe(ptr), cost, cost <= _state.Money)); v.Ok.Add(cost <= _state.Money); }
            }
            yield return HeadSay(0x4D6, 4, owner);                 // state 0: "Where should I take you?"
            if (v == null) { _routineOn = false; AdvanceScript(); yield break; }
            // state 3: createShopBitsBox(1) out of the boy, the list out of Birdramon
            var bits = ItemBox();
            if (bits != null) bits.OpenBits(DialogueOrigin(0xFD), ShopBitsText(ShopSection(owner)));
            v.Open(ScriptListView.Kind.Birdramon, DialogueOrigin(owner) ?? new RectInt(0, 0, 10, 10));
            int chosen = -1, state = 3;
            while (state == 3)
            {
                yield return null;
                if (v.Box.State != 1) continue;
                // tickBirdraTransportMenu: Cross on one the Bits pay for, Triangle leaves (state 6), up / down repeat, R1 to the first / last row
                if (ShopCross() && v.Rows.Count > 0)
                {
                    if (v.Ok[v.Row]) { Snd(3); chosen = rows[v.Row]; state = 4; }
                    else Snd(0xB);
                    continue;
                }
                if (GameMenus.TriangleOnly()) { Snd(4); state = 6; continue; }
                int step = GameMenus.RepeatStep();
                if (step < 0) { if (GameMenus.WalkHeld()) v.ToTop(); else v.Up(); }
                else if (step > 0) { if (GameMenus.WalkHeld()) v.ToBottom(); else v.Down(); }
            }
            if (state == 4)
            {
                yield return HeadSay(0x4D6, 6, owner);              // "Are you sure? Once you go, I can't take you back."
                int pick = 0;
                yield return HeadAsk(0x4D6, 7, 2, a => pick = a, 0xFD);     // the boy: "I know" / "Cancel"
                state = 5 + pick;
            }
            v.Close(v.Box);
            if (bits != null) bits.Close(bits.Bits);
            if (state == 5)
            {
                // variables 247 / 248 = map / exit, the Bits paid, and script 0 section 0x4E3 takes over (the flight)
                int o = ExeAt(BirdraTargets) + 8 * chosen;
                _state.Vars[0xF7] = exe[o]; _state.Vars[0xF8] = exe[o + 1];
                _state.Money -= System.BitConverter.ToInt32(exe, o + 4);
                _routineOn = false;
                StartDgSection(0x4E3);
                yield break;
            }
            yield return HeadSay(0x4D6, 5, owner);                 // state 6: "I think that going yourself may be the best way."
            _routineOn = false;
            AdvanceScript();
        }

        // ------------------------------------------------------------------ 0x09 Giromon's jukebox

        // BGM_TRACK_NAMES (exe 0x8012FEC8, 63 pointers) and the (font, track) of each song (exe 0x801303B8, 63 x 2 bytes)
        const uint BgmTrackNames = 0x8012FEC8, BgmTrackMusic = 0x801303B8;

        string BgmTrackName(int n)
        {
            if (n < 0 || n >= 63) return "";
            return Exe(System.BitConverter.ToUInt32(ExeBytes, ExeAt(BgmTrackNames) + 4 * n));
        }

        // ours: breaks each line at spaces so that it fits 'width' PS1 pixels of the game's font
        static string WrapLine(string text, int width)
        {
            var font = Battle.StatFont;
            if (font == null || text == null) return text;
            var outLines = new List<string>();
            foreach (var line in text.Split('\n'))
            {
                string cur = "";
                foreach (var word in line.Split(' '))
                {
                    string next = cur.Length == 0 ? word : cur + " " + word;
                    if (cur.Length > 0 && font.Measure(next) > width) { outLines.Add(cur); cur = word; }
                    else cur = next;
                }
                outLines.Add(cur);
            }
            return string.Join("\n", outLines.ToArray());
        }

        // openJukebox (states 0-4) with jukeboxFillTitleList, createJukeboxMenu and tickJukeboxMenu (shop/script_menu.c). Variable 249 = the song
        // chosen, 254 = Giromon. Texts DG.SCN 0 section 0x4D6, entries 0-3.
        IEnumerator Jukebox()
        {
            _routineOn = true;
            byte[] exe = ExeBytes;
            int owner = _state.Vars[0xFE], music = ExeAt(BgmTrackMusic);
            // jukeboxFillTitleList: every song, the last one (0x3E, Last Battle Theme) only with trigger 50 on; variable 249 = the row of the
            // music playing now, or 0xFF and the music stops
            var tracks = new List<int>();
            bool all = _state.GetFlag(50);
            for (int i = 0; i < 0x3F; i++) if (all || i != 0x3E) tracks.Add(i);
            int playing = 0xFF;
            for (int i = 0; i < tracks.Count; i++)
                if (_audio != null && exe[music + tracks[i] * 2] == _audio.ActiveFont && exe[music + tracks[i] * 2 + 1] == _audio.ActiveTrack) { playing = i; break; }
            _state.Vars[0xF9] = (byte)playing;
            if (playing == 0xFF && _audio != null) _audio.StopMusic();
            yield return HeadSay(0x4D6, 3, owner);                 // state 0: "Why don't you request a number that will make me hot."
            var v = ScriptList();
            if (v == null) { _routineOn = false; if (_audio != null) _audio.RestoreBgm(); AdvanceScript(); yield break; }
            System.Action fill = () =>
            {
                v.Rows.Clear(); v.Ok.Clear();
                foreach (int t in tracks) { v.Rows.Add(ScriptListView.JukeboxRow(t, BgmTrackName(t), t == _state.Vars[0xF9])); v.Ok.Add(true); }
            };
            fill();
            // state 3: createJukeboxMenu out of Giromon, the cursor on the song variable 249 names (row 0 if none)
            v.Open(ScriptListView.Kind.Jukebox, DialogueOrigin(owner) ?? new RectInt(0, 0, 10, 10));
            int start = tracks.IndexOf(_state.Vars[0xF9]);
            v.Place(start < 0 ? 0 : start);
            _line = HeadText(0x4D6, 0, true); _speaker = owner; _choiceOpts = null;     // "Which one do you want." stays while the list is up
            int heard = playing;                                    // MAIN_D_80134F8F
            while (true)
            {
                yield return null;
                if (v.Box.State != 1) continue;
                // tickJukeboxMenu: a new song in variable 249 starts at once (stopBGM, playMusic)
                int cur = _state.Vars[0xF9];
                if (cur != heard)
                {
                    heard = cur;
                    if (_audio != null && cur < 0x3F) { _audio.StopMusic(); _audio.PlayMusic(exe[music + cur * 2], exe[music + cur * 2 + 1]); }
                }
                if (ShopCross() && v.Rows.Count > 0)
                {
                    _state.Vars[0xF9] = (byte)tracks[v.Row];
                    // "<song>, right." (text code 0x11); ours (user's choice 2026-11-10): a name too long for the box goes on over the next lines
                    _line = WrapLine(HeadText(0x4D6, 2, true), 0x106 - 0x14); _speaker = owner; _choiceOpts = null;
                    fill();
                    Snd(3);
                    continue;
                }
                if (GameMenus.TriangleOnly()) { Snd(4); break; }
                int step = GameMenus.RepeatStep();
                if (step < 0) { if (GameMenus.WalkHeld()) v.ToTop(); else v.Up(); }
                else if (step > 0) { if (GameMenus.WalkHeld()) v.ToBottom(); else v.Down(); }
            }
            // state 4: the list shuts, "Help me on my next request."; state 2: the map's music again (playBGM(ACTIVE_BGM_FONT))
            v.Close(v.Box);
            _line = null;
            yield return HeadSay(0x4D6, 1, owner);
            if (_audio != null) _audio.RestoreBgm();
            _routineOn = false;
            AdvanceScript();
        }

        // ------------------------------------------------------------------ 0x02 Mojyamon trade

        // MOJYAMON_ITEMS_GIVE / MOJYAMON_ITEMS_GET (exe 0x80130438 / 0x80130444): three trades per Mojyamon (variable 249 = which one)
        const uint MojyaGive = 0x80130438, MojyaGet = 0x80130444;

        // openMojyamonShop (states 0-6) with mojyaTradeFillItemList, createMojyaTradeMenu, tickMojyaTradeMenu and setMojyaItemTradedTrigger
        IEnumerator MojyamonTrade()
        {
            _routineOn = true;
            byte[] exe = ExeBytes;
            int give = ExeAt(MojyaGive), get = ExeAt(MojyaGet);
            int owner = _state.Vars[0xFE];
            int type = _state.Vars[0xF9] * 3;                 // ITEM_MENU_TYPE
            System.Func<int, string> name = i => _state.ItemNameOf != null ? _state.ItemNameOf(i) : i.ToString();
            var v = ScriptList();
            if (v != null)
            {
                v.Rows.Clear(); v.Ok.Clear();
                for (int i = 0; i < 3; i++)
                {
                    int want = exe[give + type + i];
                    bool ok = _state.Bag[want] != 0;
                    v.Rows.Add(ScriptListView.MojyaRow(name(want), name(exe[get + type + i]), ok, _state.GetFlag(type + i + 5)));
                    v.Ok.Add(ok);
                }
            }
            _state.Vars[0xF9] = 0xFF;
            yield return HeadSay(0x4D6, 8, owner);                 // "I'm a collector. If you got a thing I want, I'll trade."
            if (v == null) { _routineOn = false; AdvanceScript(); yield break; }
            // state 3: the list out of Mojyamon, "What will you give me?" stays up behind it
            v.Open(ScriptListView.Kind.Mojyamon, DialogueOrigin(owner) ?? new RectInt(0, 0, 10, 10));
            _line = HeadText(0x4D6, 9, true); _speaker = owner; _choiceOpts = null;
            int state = 3, row = 0;
            while (state == 3)
            {
                yield return null;
                if (v.Box.State != 1) continue;
                if (ShopCross())
                {
                    if (v.Ok[v.Row]) { Snd(3); row = v.Row; _state.Vars[0xF9] = exe[give + type + row]; state = 4; }
                    else Snd(0xB);
                    continue;
                }
                if (GameMenus.TriangleOnly()) { Snd(4); state = 6; continue; }
                int step = GameMenus.RepeatStep();
                if (step < 0) { if (GameMenus.WalkHeld()) v.ToTop(); else v.Up(); }
                else if (step > 0) { if (GameMenus.WalkHeld()) v.ToBottom(); else v.Down(); }
            }
            _line = null;
            if (state == 4)
            {
                yield return HeadSay(0x4D6, 0xB, owner);           // "You'll really give me <item>?"
                int pick = 0;
                yield return HeadAsk(0x4D6, 0xC, 2, a => pick = a, 0xFD);     // the boy: "Yeah" / "Well.... no"
                state = 5 + pick;
            }
            v.Close(v.Box);
            if (state == 5)
            {
                if (_state.AddItem(exe[get + type + row], 1))
                {
                    _state.RemoveItem(_state.Vars[0xF9], 1);
                    _state.SetFlag(type + row + 5, true);           // setMojyaItemTradedTrigger: flags 5-13, all of them = flag 14
                    bool all = true;
                    for (int f = 5; f < 0xE; f++) if (!_state.GetFlag(f)) all = false;
                    if (all) _state.SetFlag(0xE, true);
                    _state.SyncSlots();
                    ShopSaleSound();
                    yield return HeadSay(0x4D6, 0xD, owner);       // "Thanks."
                }
                else { ShopSaleSound(); yield return HeadSay(0x4D6, 0xE, owner); }      // "Too many Items."
            }
            else yield return HeadSay(0x4D6, 10, owner);            // state 6: "I'll trade all the time, so bring whatever you got."
            _routineOn = false;
            AdvanceScript();
        }

        // the boxes the routines draw on the game's UI boxes
        void DrawRoutineList()
        {
            if (_itemBox != null) _itemBox.Draw();
            if (_keeper != null) _keeper.Draw();
            if (_scriptList != null) _scriptList.Draw();
        }
    }
}
