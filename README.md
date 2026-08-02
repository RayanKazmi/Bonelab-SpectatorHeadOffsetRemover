# Spectator Head Offset Remover
This is a Bonelab code mod that removes the head offset added by marrow (to prevent your own face being in view) from the spectator camera on pcvr. It works by making a clone of your avatar and displaying it instead of the offset avatar. The clone is only visible on the pc side so your vr gameplay is uninterrupted and you can use 3rd person camera mods such as wide eye or advance gopro to record yourself without the offset!

## DISCLAIMER
This mod was made by ai (Claude sonnet 5) using the dll from https://thunderstore.io/c/bonelab/p/notnotnotswipez/HeadOffsetFixer/ as a base. This mod being ai made is the reason its open source so people can use the code or make changes and optimizations to it and this mod.

## Q&A
Q: Why is this mod Ai generated? A: I was too lazy and had another, more important, project I had to work on. I can code in c tho.<br>
Q: Can this mod be used on quest A: No<br>
Q: How do I compile the mod A: open the folder with both files in terminal, run `$env:BONELAB_DIR = "PathToFolderContaningYourModsFolder"` and then run `dotnet build -c Release` in to compile in into your mods folder
