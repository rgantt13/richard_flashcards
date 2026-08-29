This implementation is not working currently. Unsure of if the current implementation has .sqlite persist. My understanding is that it should be capable of doing this without a hosted instance on my machine.
Persistance may simply be broken at the moment.

Subjects do not need their own view for management. Subjects should simply be a text tag. 
From the Creation panel, the subject field should be a combobox where a user can type out a new subject to create it on the spot or select a pre-existing subject from an auto-completing dropdown.

The Creation panel for a custom flashcard should look more like a designer than a form in this way. It should look more similar to https://excalidraw.com or Microsoft Publish than the existing implementation.
The multiple choice selection should have a separate, stylized designer with predefined sections to type in or drag/drop images in for up to 4 different answer sections and 1 question section.
Cloze flashcards should have their own designer customize for the fill-in-the-blank configurations needed in making the card.

It looks like we got lazy creating the application handlers and stopped separating query from command handlers. Please separate them appropriately.