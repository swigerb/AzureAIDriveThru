# Demo quick-start

Run these commands from the repository root before presenting the experience live:

```bash
azd auth login
./scripts/start.sh
```

## Demo Mode

Open **Settings** and turn on **Demo Mode**. If the active persona ships
`assets/demo/guestScript.json`, the center controls show **Run demo: This brand**
and **Full tour**. The demo starts a fresh order, feeds a synthetic mic stream
with the scripted guest clips, plays the guest voice to the room, and advances
only after assistant playback has been quiet long enough for a complete turn.
Use **Stop demo** to end the conversation cleanly.

## Sample talk track

- Hey what's up.
- We would like to order some drinks but we haven't decided yet, would you like to recommend some?
- The weather is really cool outside.
- I would like that.
- My friend does not want a slush, could you recommend something other than a slush?
- Apple juice sounds good.
- Also, I have another friend, he is really hangry.
- Great, one of the two.
- We want the sandwich.
- How much is the price?
- That's all, thank you very much.
