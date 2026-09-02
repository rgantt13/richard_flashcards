# Generating decks with an AI

The app imports `.fcdeck` files — readable JSON. That makes bulk card creation a matter of asking a
language model for the right JSON and importing the result, and the app ships the prompt that does
it.

The app itself never calls a model, has no API key and makes no network requests. It builds the
prompt; you paste it wherever you like, including into something running on your own machine.

## How to use it

1. **Manage → Generate**. Type the subject, choose how many cards, and add an optional steer
   ("assume I know the basics", "lean on the syntax").
2. **Copy prompt**, then paste it into ChatGPT, Claude, Gemini or a local model.
3. Save the reply as `my-deck.fcdeck` — plain text, nothing else in the file.
4. **Manage → Import**, pick the file, tick what you want, press Import.

Import is not all-or-nothing. You choose which subjects and cards to bring in, whether to skip or
replace anything you already have, and any card the model got wrong is skipped with a reason rather
than taking the rest down with it. Read the warnings; they name the card.

If you are trying an unfamiliar deck, run against a throwaway library first — see
[Running against a different library](../README.md#running-against-a-different-library).

---

## The prompt

The prompt lives in one place: [`src/Flashcards.Desktop/Assets/DeckPrompt.txt`](../src/Flashcards.Desktop/Assets/DeckPrompt.txt).
The dialog reads that file at runtime and fills in three placeholders — `<<SUBJECT>>`, `<<COUNT>>`
and `<<FOCUS>>` — so what you copy is exactly what is in the repository. It is not reproduced here
on purpose: a second copy would drift from the first, and a prompt that documents a schema is only
useful while it is telling the truth about it.

Read it if you want to change how decks come out. It covers, in order:

- the JSON shape, and which properties may be omitted;
- the hard rules — the ones that make the importer skip a card;
- how to build the subject tree, and to tag each card with the most specific subject that fits;
- what each card type is *for*, which is the part that decides whether a deck is worth studying;
- four worked examples, one per card type.

Those four examples are not decoration. `tests/Flashcards.Integration.Tests/GeneratedDeckTests.cs`
runs them verbatim through the real reader and the real importer and asserts that they arrive with
no warnings. If you edit the prompt's schema, that test is what tells you whether the app still
agrees with it.

You can also use the prompt outside the app: copy the file, replace the three placeholders by hand,
and delete the `<<FOCUS>>` line if you have nothing to add.

---

## When it goes wrong

**"That file could not be read as a flashcards deck…"** — the JSON did not parse, or a value did
not fit. The message carries the parser's own complaint, which locates the problem by path — for
example `$.Cards[1].CardType`, meaning the second card's type. It names the path and the expected
type, not the offending value, so look at that property in that card.

The most common cause is a misspelled enum: `"MultiChoice"` instead of `"MultipleChoice"`, or
`"Text"` instead of `"PlainText"`.

**Cards skipped with warnings** — the file was fine but individual cards broke a rule. The warning
names the card and the rule. Common ones: a multiple-choice card where every option is correct, a
cloze card with no `{{blanks}}`, a standard card with no answer block.

**Everything imported but the tree is flat** — the model tagged every card with the top-level
subject. Ask it again, insisting each card carries the most specific subject that fits.

**Duplicate names** — cards are matched by name plus a shared subject, so two generated cards with
the same name under the same subject collide. Import reports the second as skipped.
