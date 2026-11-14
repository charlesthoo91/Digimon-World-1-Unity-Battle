==========================================================================
                    DIGIMON WORLD 1 UNITY BATTLE
                    CT Version 1.0.0  -  Player's guide
==========================================================================

A fan remake of the PlayStation game Digimon World (1999), rebuilt in the
Unity engine. It plays the original game, from the first day in File City
to the ending. It also adds optional extras: widescreen, AI upscaling,
online battles and an online tournament for up to 8 players, a Traditional
Chinese translation, and more.

This is a fan project. It is not made, approved or supported by Bandai
Namco. It comes with NO game data: no maps, no Digimon, no music and no
text from the original game. The game reads all of them, each time it
runs, from YOUR OWN copy of the original disc.


--------------------------------------------------------------------------
CONTENTS
--------------------------------------------------------------------------
  1. What you need
  2. First start: choosing your disc
  3. Controls
  4. Saves
  5. AI Upscale (sharper pictures) - install Upscayl first
  6. PREPARE AI UPSCALE (do the upscaling once, before playing)
  7. The extra features (OPTIONS on the title screen)
  8. Online battle (1 vs 1 with a friend)
  9. Online tournament (up to 8 players)
 10. Problems and answers


--------------------------------------------------------------------------
1. WHAT YOU NEED
--------------------------------------------------------------------------
  - A Windows 10 or 11 PC (64-bit).
  - Your own copy of the USA PlayStation disc of Digimon World,
    serial SLUS-01032, as a disc image file:
        .bin  (with or without its .cue)   <- best: the movies have sound
        .iso
        .cue  (the game opens the .bin the .cue points to)
    Only the USA disc is accepted for now. The game checks the disc and
    tells you if it is a different version (European, Japanese) or a
    modified copy.
  - For AI Upscale only: a graphics card and the free program Upscayl
    (see section 5).
  - For online play only: an internet connection.

  Make the disc image from a disc you own, with a disc-copying program.
  Do not ask for the disc image online.


--------------------------------------------------------------------------
2. FIRST START: CHOOSING YOUR DISC
--------------------------------------------------------------------------
  1. Unzip the game folder anywhere (for example D:\Games\DW1UnityBattle).
     Keep all the files together.
  2. Double-click DW1UnityBattle.exe.
  3. The first time, the game asks for your disc. Pick your .bin, .iso
     or .cue file in the window that opens.
  4. The game checks the disc. If it is the right one, the opening movie
     and the title screen appear.

  The game remembers where your disc file is. If you move or rename the
  file later, the game asks for it again.

  Keep the disc file where it is: the game reads from it every time you
  play. It never changes your disc file.


--------------------------------------------------------------------------
3. CONTROLS
--------------------------------------------------------------------------
  A game controller works, and so does the keyboard. You can change every
  key and button in OPTIONS > CONTROLS.

  Default keyboard keys:

     PlayStation button     Keyboard
     ------------------     ------------------------------
     D-pad                  Arrow keys
     Cross    (X)           Space
     Circle   (O)           Z
     Square                 X
     Triangle               Tab, C or Esc
     Start                  Enter
     Select                 Backspace
     L1 + R1                Q + E
     L2 + R2                1 + 3   (online battle: surrender)

  Esc while you walk freely goes back to the title screen.

  Two copies of the game at most can run on one PC at the same time
  (handy for trying online play on one PC). A third copy says so and
  closes.


--------------------------------------------------------------------------
4. SAVES
--------------------------------------------------------------------------
  You save the way you do in the original: at the save machine in
  Jijimon's house, or when your partner sleeps. There are two "memory
  cards" of 15 slots each.

  The saves are kept here:
      C:\Users\<your name>\AppData\LocalLow\DW1UnityBattle\DW1UnityBattle

  OPTIONS > EXPORT SAVE copies a slot to a .json file, to keep it safe or
  move it to another PC. The files go to:
      C:\Users\<your name>\Documents\DW1UnityBattle

  OPTIONS > IMPORT SAVE loads one of these:
    - a .json save file from EXPORT SAVE, or
    - a save from a real PS1 memory card file or emulator memory card
      (.mcd, .mcr, .mc, .srm, .gme, .vmp, .mcs).
  You can put it into any slot. If that slot already holds a save, the
  game asks Yes / No first.


--------------------------------------------------------------------------
5. AI UPSCALE (SHARPER PICTURES) - INSTALL UPSCAYL FIRST
--------------------------------------------------------------------------
  The original pictures are small (320 x 240 screens, small Digimon
  textures). AI Upscale makes them four times sharper, on your own
  graphics card. It uses the AI model "Remacri" that comes with the free
  program Upscayl. The game does not include the model: you install
  Upscayl, and the game finds it there.

  How to install Upscayl at the default place:
  1. Go to the official site:  https://upscayl.org
     (or https://github.com/upscayl/upscayl/releases)
  2. Download the Windows installer (.exe).
  3. Run it. When it asks, choose to install for ALL USERS, and keep the
     folder it suggests:
         C:\Program Files\Upscayl\
     (A per-user install in
         C:\Users\<your name>\AppData\Local\Programs\Upscayl\
      is also found. Any other folder is not.)
  4. You do not need to open Upscayl itself.
  5. Start the game: title screen > OPTIONS > AI UPSCALE > ON.
     The game finds the model and asks whether to prepare every picture
     now (see section 6).

  IMPORTANT - THE GAME WILL STUTTER THE FIRST TIME
  -------------------------------------------------
  Without PREPARE AI UPSCALE, each picture is upscaled the FIRST time the
  game needs it. While that runs (a few seconds, depending on your
  graphics card), the game can stutter or pause, and the picture may show
  blurry first, then sharp. This happens the first time:
    - you enter each map (area),
    - you see each map at each time of day: DAY, DUSK and NIGHT use
      different colours, so each one is a separate picture,
    - each kind of Digimon appears (your partner, map Digimon, enemies),
    - each small picture or map piece is shown.
  Every finished picture is saved on your PC. Next time it loads at
  once, with no stutter. The saved pictures are here:
      C:\Users\<your name>\AppData\LocalLow\DW1UnityBattle\DW1UnityBattle\AiUpscale
  (Delete that folder to free the space. The game then upscales again
  when needed.)

  AI Upscale is for the PC version only.


--------------------------------------------------------------------------
6. PREPARE AI UPSCALE (DO THE UPSCALING ONCE, BEFORE PLAYING)
--------------------------------------------------------------------------
  This works like a game that builds its "shader cache" before you play:
  it upscales everything big at once, so you get little or no stutter
  while playing.

  Title screen > OPTIONS > PREPARE AI UPSCALE
  (AI UPSCALE must be ON. The game also offers it when you switch
  AI UPSCALE on.)

  - It goes through every map, at every time of day, and every Digimon's
    texture sheet.
  - Before it starts it tells you how many pictures are left, about how
    much disk space they need (about 6 to 7 GB for everything), and how
    much space your disk has. Then it asks you to confirm.
  - While it runs you see the percent done, the time left (estimated
    after a few seconds), the number of maps and Digimon done, and a
    progress bar.
  - It can take a long time, depending on your graphics card. The time
    left is shown once it has measured your card's speed. Triangle stops it at any time. Everything finished so
    far is kept. Choose PREPARE AI UPSCALE again later to carry on from
    where it stopped.

  What it does NOT prepare: MAP PIECES and other small pictures. Map
  pieces are the parts of a map drawn over the background (doors, moving
  parts, things that open or change during the story). Which ones show,
  and how, depends on where you are in the game. They are small, so they
  are still upscaled the first time they appear. Expect a short hitch
  then; after that they are saved like everything else.


--------------------------------------------------------------------------
7. THE EXTRA FEATURES (OPTIONS ON THE TITLE SCREEN)
--------------------------------------------------------------------------
  Every extra is OFF by default. With everything off, the game plays like
  the original. Choose OPTIONS on the title screen. Each option has a
  short help line at the bottom of the page.

  DIGITAL CLOCK
      Shows the time as numbers under the clock dial.

  ONLINE BATTLE
      Turns on online play at Greymon's arena: a 1 vs 1 battle with a
      friend, or a tournament of up to 8 players (sections 8 and 9).

  SMOOTH 60 FPS
      Draws up to 60 pictures a second instead of the original 20. The
      game itself keeps its original speed (clock, fights, timers).

  EVOLUTION GUIDE
      On the Chart page, press Square to see what each evolution needs,
      your partner's numbers next to them, and which Digimon your partner
      would evolve into now.

  RECRUIT TAB
      Adds a fifth tab, "Rec.", to the Player menu: every Digimon that
      can join File City (joined ones in green), the prosperity each one
      brings, and the city's prosperity now.

  STATUS NUMBERS
      The Status page shows happiness, discipline and virus as numbers on
      their bars, and the hours of life your partner has left.

  TENSION VALUE
      Fishing: shows the line tension as a number (0 to 100 percent)
      under the gauge.

  FISHING INDICATOR
      Fishing: shows "PRESS X" over the float while a bite can be hooked.

  MAP
      Press Select while walking to see a map of the area: the exits, the
      Digimon and where you are.

  RECENTRE CAMERA
      In the original, the camera stays off-centre after a fight. With
      this on, it glides back to the boy after you win a wild battle.

  AI UPSCALE / PREPARE AI UPSCALE
      Sharper maps, Digimon and pictures (sections 5 and 6).

  ASPECT RATIO
      4:3 like the original, or 16:9 to see more of the map at the sides.
      The menus and HUD stay in the 4:3 middle.

  SCREEN MODE
      Window, Borderless (fills the screen, quick to switch to other
      programs) or Full screen.

  RESOLUTION
      The picture's size: your screen's own, 720p, 1080p, 1440p or 4K.

  VOLUME
      Left / Right: 0 to 100.

  LANGUAGE
      English or Traditional Chinese (繁體中文) for the whole game:
      dialogue, menus, item and Digimon names. Names you typed yourself
      stay as you typed them.

  CHEATS  (a page of its own; they act when you LOAD a saved game)
      ITEM BANK 99    99 of every item in Agumon's item bank, once Agumon
                      has joined the city (story items left out).
      FULL CHART      every Digimon on the evolution chart counts as
                      raised.
      ALL MEDALS      every medal is won.
      ALL CARDS       at least 1 of each card.
      MAX BITS        999,999 Bits.
      ALL TECHNIQUES  every technique is learned.
      MAX STATS       HP and MP 9999; Offense, Defense, Speed and Brains
                      999.
      PASS AN HOUR    L1 + R1 + Circle while the clock runs moves time on
                      by one hour.
      Cheats change your save once you save again. Export the save first
      (EXPORT SAVE) if you want to keep a clean copy.

  CONTROLS
      Change the keyboard keys, controller buttons and touch buttons.

  EXPORT SAVE / IMPORT SAVE
      Copy saves to and from files, including PS1 memory card files
      (section 4).


--------------------------------------------------------------------------
8. ONLINE BATTLE (1 VS 1 WITH A FRIEND)
--------------------------------------------------------------------------
  Your partner fights a friend's partner, over the internet, in Greymon's
  arena: introductions, BATTLE, WIN / LOSE, just like a tournament match.

  Good to know:
  - It is a friendly match. NOTHING in either save changes: stats,
    techniques, Bits, condition and mood are all put back afterwards.
  - The two PCs link directly to each other (no game server in between).
    You swap two codes, by chat or e-mail, to make the link.
  - Windows may ask whether to let DW1UnityBattle through the firewall.
    Choose Allow. Without it, the link cannot start.
  - A PC can also link with a friend playing in a web browser.

  Both players:
  1. Title screen > OPTIONS > ONLINE BATTLE > ON.
  2. Load your game and go to Greymon's arena in File City (Greymon must
     have joined the city).
  3. Talk to Greymon at his counter (Cross). He asks:
        Online / Tournament / Leave            -> choose Online
        Online battle / Online tournament      -> choose Online battle
  4. One of you HOSTS, the other JOINS.

  THE HOST:
  5. Choose "Host. I make the first code." Wait a moment: your code
     appears (letters and digits in groups of five).
  6. Choose "Copy my code" and send it to your friend (Discord, chat,
     e-mail...).
  7. Wait for your friend's REPLY code. Copy it on your PC, then choose
     "Paste reply code".

  THE FRIEND WHO JOINS:
  5. Choose "Join. My friend has a code."
  6. Copy the host's code on your PC, then choose "Paste friend's code".
  7. Your reply code appears. Choose "Copy my code" and send it back to
     the host. The link starts when the host pastes it.

  WHEN LINKED:
  8. Both screens show "Linked", the ping, and the other player's Digimon
     (name, HP, MP, Offense, Defense, Speed, Brains).
  9. The host chooses:
       Orders   ON / OFF   (OFF: both Digimon fight on their own Brains,
                            like tournament opponents)
       Time     99 s (the arena's), 300 s or No limit
 10. Both choose "Battle!". The match starts when both have chosen it.
 11. During the match, L2 + R2 (keys 1 + 3) asks "Surrender?". Yes ends
     the match as your loss.
 12. Afterwards you are back on the link screen. Choose Battle! again
     for another match, or Close.

  If the link fails ("could not be reached", "firewall"): choose
  "Try again" and swap new codes. Codes are used once.


--------------------------------------------------------------------------
9. ONLINE TOURNAMENT (UP TO 8 PLAYERS)
--------------------------------------------------------------------------
  A knock-out tournament like the arena's cups: 8 places, three rounds
  (quarter-final, semi-final, final). Players who are online take places.
  Any empty places become computer Digimon. Nobody copies codes: a free
  online service (running on Cloudflare) lists the open tournaments and
  links everyone automatically.

  Good to know:
  - Free for all: any Digimon may enter.
  - Nothing in anyone's save changes. There are no prizes.
  - Orders are ON in every match. A match lasts the arena's 99 seconds.
    A match between two players that is still level then goes to
    SUDDEN DEATH (double damage, no blocking) until a knock-out.
  - Every match is played on its own players' PCs, and all matches of a
    round run at the same time. The next round starts when they are all
    over.
  - The host fights too. If the host leaves, the tournament ends for
    everyone. A player whose link to the host is lost loses their match.
  - Allow the game through the Windows firewall if asked.

  Everyone:
  1. OPTIONS > ONLINE BATTLE > ON. Load your game, go to Greymon's arena
     and talk to Greymon.
  2. Choose Online > Online tournament.

  TO HOST:
  3. Choose Host. Then set:
       Computer strength   how strong the computer Digimon are
                           (D, C, B or A, like the arena's cups). The
                           computer Digimon are taken from those that
                           have joined File City in YOUR game.
       Password            OFF, or ON for a game with friends only
                           (a new random 4-digit number; tell it to your
                           friends).
  4. Choose "Open the tournament". You see the tournament's code and the
     list of players as they join. "Copy my code" copies the code, to
     send to your friends (they use Join > Find by code).
  5. Choose Start when you are ready. If places are still empty, the game
     asks first: "Start now? The empty places get computer Digimon."
     (No / Yes). (When all 8 places are full, it starts by itself after
     30 seconds.)

  TO JOIN:
  3. Choose Join. You see the open tournaments: code, host's name,
     players "n of 8", and "Password" if it has one.
     (Choose Refresh to look again.)
     When there are more than fit in the box, the list scrolls: Up / Down
     moves through it, Left / Right turns a whole page, and the top line
     says which ones you see (for example "1-5 of 12"). Refresh, Find by
     code and Back always stay under the list.
  4. Pick one. If it has a password: set the 4 digits (Up / Down picks a
     digit, Left / Right or Cross changes it), then choose Join.
     OR choose "Find by code" to go straight to a friend's tournament:
       - Ask the host for the 6-letter code (the host sees it at the top
         of their tournament screen, "Code ...").
       - Up / Down picks a letter's place, Left / Right or Cross changes
         it. Or copy the code on your PC and choose "Paste code".
       - Choose Find. The game joins that tournament (asking for its
         password first if it has one), or says that no open tournament
         has that code. Triangle goes back to the list.
  5. Wait for the host to choose Start.

  DURING THE TOURNAMENT:
  - Everyone sees the cup's title and the bracket, then each round's
    animated bracket.
  - When your match comes, it starts by itself.
  - In any of your matches (against a player or a computer Digimon),
    L2 + R2 (keys 1 + 3) asks "Surrender?". Yes ends the match as your
    loss; No or Triangle closes the question and the fight goes on.
  - Between matches, the board shows the results (WIN / LOSE).
  - Waiting, or knocked out? Choose "Watch" to watch another match of the
    round live.
  - The champion gets the arena's champion scene and podium.
  - Close (or "Close the tournament" for the host) leaves. The game asks
    first. The host leaving ends the tournament for everyone.

  To try it on your own, you can run two copies on one PC (section 3).
  A tournament whose host has been away for 3 minutes is closed by the
  service.


--------------------------------------------------------------------------
10. PROBLEMS AND ANSWERS
--------------------------------------------------------------------------
  "This is not the USA disc" / the disc is refused
      Only the USA disc (SLUS-01032) works for now. A patched or
      translated disc image may be refused.

  The movies have no sound
      Use a .bin (raw) disc image, not an .iso. An .iso does not keep the
      movies' sound.

  AI UPSCALE says the model is not found
      Install Upscayl in C:\Program Files\Upscayl\ (section 5), then turn
      AI UPSCALE on again.

  The game stutters when I enter a new place or the time of day changes
      That is AI Upscale working on a new picture (section 5). Run
      PREPARE AI UPSCALE once (section 6), or turn AI UPSCALE off.

  Online: "could not be reached" / "firewall"
      Allow DW1UnityBattle in Windows Defender Firewall (Windows Security
      > Firewall & network protection > Allow an app through firewall),
      then try again with new codes. Some school, office or mobile
      networks block direct links between PCs.

  Online: "Your friend has a different version of the game"
      Both players need the same version (shown at the bottom right of
      the title screen).

  Where is the game's log, if something goes wrong?
      C:\Users\<your name>\AppData\LocalLow\DW1UnityBattle\DW1UnityBattle\Player.log

  Does the author have any future plan to include more Digimon in the
  build?
      No. Many builds with more Digimon are already available on the
      internet. This build stays as close to the original as possible,
      apart from quality-of-life improvements, and puts online battles
      and the online tournament first for now.

==========================================================================
  Digimon and Digimon World are trademarks of Bandai Namco. This is a
  free, unofficial fan project. Please support the original game.
==========================================================================
