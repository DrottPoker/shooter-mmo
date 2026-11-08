using System;
using System.Collections.Generic;
using Mmorpg.Client.Items;

namespace Mmorpg.Client.Quests
{
    // The game messages of the quest state: what QuestNotices tells for every committed change,
    // and what QuestProgressWatcher tells for the progress that carried items make. It starts
    // from the quest log and the inventory as they are when it is created, which may be after a
    // login loaded both, so the first change after that tells as much as any later one. A load
    // of the quest log, and the carried items of an inventory that loads after the log, start it
    // over without messages.
    public sealed class QuestNoticeFeed : IDisposable
    {
        private readonly QuestClientState quests;
        private readonly InventoryClientState inventory;
        private readonly Action<IReadOnlyList<string>> say;
        private readonly QuestProgressWatcher watcher = new QuestProgressWatcher();
        private int watchedLoad;

        // Without an inventory nothing counts as carried.
        public QuestNoticeFeed(
            QuestClientState quests,
            InventoryClientState inventory,
            Action<IReadOnlyList<string>> say)
        {
            this.quests = quests ?? throw new ArgumentNullException(nameof(quests));
            this.inventory = inventory;
            this.say = say ?? throw new ArgumentNullException(nameof(say));
            quests.Changed += OnQuestStateChanged;
            quests.QuestsChanged += OnQuestsChanged;
            if (inventory != null)
            {
                inventory.Changed += OnInventoryChanged;
            }

            StartOver();
        }

        // The carried items count once the inventory holds the character's confirmed state.
        private bool CarriedItemsKnown
        {
            get
            {
                return inventory == null
                    || (inventory.ConfirmedSnapshot != null && inventory.CharacterId == quests.CharacterId);
            }
        }

        public void Dispose()
        {
            quests.Changed -= OnQuestStateChanged;
            quests.QuestsChanged -= OnQuestsChanged;
            if (inventory != null)
            {
                inventory.Changed -= OnInventoryChanged;
            }
        }

        private void StartOver()
        {
            watchedLoad = quests.LoadCount;
            watcher.Update(quests, QuestCarriedItems.Counter(inventory), CarriedItemsKnown);
        }

        // A load starts the watch over before the changes that follow it.
        private void OnQuestStateChanged()
        {
            if (quests.LoadCount != watchedLoad)
            {
                StartOver();
            }
        }

        private void OnQuestsChanged(IReadOnlyList<QuestClientChange> changes)
        {
            say(QuestNotices.For(changes, quests.Catalog));
            Watch();
        }

        // Carried items move collect objectives, and they can make a quest ready.
        private void OnInventoryChanged()
        {
            Watch();
        }

        private void Watch()
        {
            say(watcher.Update(quests, QuestCarriedItems.Counter(inventory), CarriedItemsKnown));
        }
    }
}
