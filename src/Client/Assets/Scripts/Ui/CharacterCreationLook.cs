using System;
using System.Collections.Generic;
using Mmorpg.Client.Api;

namespace Mmorpg.Client.Ui
{
    // The body model and look the character creation rows choose, from the offers GameService
    // sends: one option per kind and slot the body model offers, the first by default, and none
    // allowed for a kind a character may go without, such as a bald head. Another body model starts
    // from its own defaults. Temporary until the custom character creation screen.
    public sealed class CharacterCreationLook
    {
        private readonly CharacterAppearanceCatalogResponse catalog;
        private readonly Dictionary<string, bool> requiredKinds = new Dictionary<string, bool>(StringComparer.Ordinal);
        // The option index of every offer of the body model, -1 for none.
        private int[] chosen = new int[0];
        private int bodyIndex;

        public CharacterCreationLook(CharacterAppearanceCatalogResponse appearanceCatalog)
        {
            catalog = appearanceCatalog;
            if (catalog != null && catalog.kinds != null)
            {
                foreach (var kind in catalog.kinds)
                {
                    if (kind != null && !string.IsNullOrEmpty(kind.id))
                    {
                        requiredKinds[kind.id] = kind.required;
                    }
                }
            }

            ResetChoices();
        }

        public bool IsAvailable
        {
            get { return BodyModel != null; }
        }

        public int BodyModelCount
        {
            get { return catalog != null && catalog.bodyModels != null ? catalog.bodyModels.Length : 0; }
        }

        public string BodyModel
        {
            get { return BodyModelCount > 0 ? catalog.bodyModels[bodyIndex].id : null; }
        }

        public int OfferCount
        {
            get { return chosen.Length; }
        }

        // Such as "underwear (legs)".
        public string OfferLabel(int offer)
        {
            var found = Offer(offer);
            return found.kind + (string.IsNullOrEmpty(found.slot) ? string.Empty : " (" + found.slot + ")");
        }

        // The chosen option of the offer, or "none".
        public string OptionLabel(int offer)
        {
            var index = chosen[offer];
            return index < 0 ? "none" : Offer(offer).options[index];
        }

        public void StepBodyModel(int step)
        {
            if (BodyModelCount < 2 || step == 0)
            {
                return;
            }

            bodyIndex = Wrap(bodyIndex + step, BodyModelCount);
            ResetChoices();
        }

        // Steps through the offer's options, through none for a kind that is not required.
        public void StepOption(int offer, int step)
        {
            var found = Offer(offer);
            var optional = !IsRequired(found.kind);
            var count = (found.options != null ? found.options.Length : 0) + (optional ? 1 : 0);
            if (count < 2 || step == 0)
            {
                return;
            }

            var position = chosen[offer] + (optional ? 1 : 0);
            position = Wrap(position + step, count);
            chosen[offer] = position - (optional ? 1 : 0);
        }

        public CharacterAppearanceRequest ToRequest()
        {
            var choices = new List<CharacterAppearanceChoiceRequest>();
            for (var offer = 0; offer < chosen.Length; offer++)
            {
                if (chosen[offer] < 0)
                {
                    continue;
                }

                var found = Offer(offer);
                choices.Add(new CharacterAppearanceChoiceRequest
                {
                    kind = found.kind,
                    slot = found.slot ?? string.Empty,
                    option = found.options[chosen[offer]]
                });
            }

            return new CharacterAppearanceRequest { bodyModel = BodyModel, choices = choices.ToArray() };
        }

        // The defaults of the body model: the first option of every required kind, none otherwise.
        private void ResetChoices()
        {
            var offers = BodyModelCount > 0 ? catalog.bodyModels[bodyIndex].offers : null;
            chosen = new int[offers != null ? offers.Length : 0];
            for (var offer = 0; offer < chosen.Length; offer++)
            {
                var found = offers[offer];
                chosen[offer] = found != null && found.options != null && found.options.Length > 0 && IsRequired(found.kind)
                    ? 0
                    : -1;
            }
        }

        private CharacterAppearanceOfferResponse Offer(int offer)
        {
            return catalog.bodyModels[bodyIndex].offers[offer];
        }

        private bool IsRequired(string kind)
        {
            return kind != null && requiredKinds.TryGetValue(kind, out var required) && required;
        }

        private static int Wrap(int value, int count)
        {
            return ((value % count) + count) % count;
        }
    }
}
