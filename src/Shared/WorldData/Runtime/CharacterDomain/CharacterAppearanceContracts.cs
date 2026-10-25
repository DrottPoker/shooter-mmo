using System;
using System.Collections.Generic;
using System.Linq;

namespace Mmorpg.WorldData.Characters
{
    public static class CharacterAppearanceFormat
    {
        public const int Version = 1;
        public const string FileName = "appearance.json";
        public const string DirectoryName = "characters";

        public const int MaximumKinds = 16;
        public const int MaximumBodyModels = 16;
        public const int MaximumOffers = 16;
        public const int MaximumOptions = 32;
        public const int MaximumVisibleEquipmentSlots = 8;

        // The protocol carries every name, so they fit its limits.
        public const int MaximumKindLength = 32;
        public const int MaximumSlotLength = 32;
        public const int MaximumNameLength = 64;

        // The kinds the clients give a meaning to; content may not name others.
        public const string Face = "face";
        public const string Eyes = "eyes";
        public const string Scar = "scar";
        public const string Tattoo = "tattoo";
        public const string Underwear = "underwear";
        public const string Hair = "hair";

        public static readonly string[] KnownKinds = { Face, Eyes, Scar, Tattoo, Underwear, Hair };
    }

    // How a character looks, as content offers it: the kinds of its look with their rules, each
    // body model with the options it offers per kind and slot, the first of each its default and
    // the first body model the default one, and the equipment slots whose items other players
    // see. See docs/EQUIPMENT_VISUALS_DESIGN.md, The Look On The Skin.
    [Serializable]
    public sealed class CharacterAppearanceDocument
    {
        public int formatVersion;
        public List<CharacterAppearanceKindDocument> kinds = new List<CharacterAppearanceKindDocument>();
        public List<CharacterBodyModelDocument> bodyModels = new List<CharacterBodyModelDocument>();
        public List<string> visibleEquipmentSlots = new List<string>();
    }

    [Serializable]
    public sealed class CharacterAppearanceKindDocument
    {
        public string id = string.Empty;

        // Exactly one per slot a body offers when true, at most one otherwise.
        public bool required;

        // Chosen in character creation, and changed in the game later when true.
        public bool changesInGame;
    }

    [Serializable]
    public sealed class CharacterBodyModelDocument
    {
        public string id = string.Empty;
        public List<CharacterAppearanceOfferDocument> offers = new List<CharacterAppearanceOfferDocument>();
    }

    [Serializable]
    public sealed class CharacterAppearanceOfferDocument
    {
        public string kind = string.Empty;

        // The visual slot of a kind worn per slot, such as underwear in legs; empty otherwise.
        public string slot = string.Empty;

        public List<string> options = new List<string>();
    }

    public sealed class CharacterAppearanceValidationException : Exception
    {
        public CharacterAppearanceValidationException(string message)
            : base(message)
        {
        }
    }

    // One choice of a look: an option of a kind, in a slot for a kind worn per slot.
    public readonly struct CharacterAppearanceChoice : IEquatable<CharacterAppearanceChoice>
    {
        public CharacterAppearanceChoice(string kind, string slot, string option)
        {
            Kind = kind ?? string.Empty;
            Slot = slot ?? string.Empty;
            Option = option ?? string.Empty;
        }

        public string Kind { get; }

        public string Slot { get; }

        public string Option { get; }

        public bool Equals(CharacterAppearanceChoice other)
        {
            return string.Equals(Kind, other.Kind, StringComparison.Ordinal)
                && string.Equals(Slot, other.Slot, StringComparison.Ordinal)
                && string.Equals(Option, other.Option, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is CharacterAppearanceChoice other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (StringComparer.Ordinal.GetHashCode(Kind) * 31 + StringComparer.Ordinal.GetHashCode(Slot)) * 31
                + StringComparer.Ordinal.GetHashCode(Option);
        }

        public override string ToString()
        {
            return Kind + (Slot.Length > 0 ? "(" + Slot + ")" : string.Empty) + "=" + Option;
        }
    }

    public sealed class CharacterAppearanceKind
    {
        public CharacterAppearanceKind(string id, bool required, bool changesInGame)
        {
            Id = id;
            Required = required;
            ChangesInGame = changesInGame;
        }

        public string Id { get; }

        public bool Required { get; }

        public bool ChangesInGame { get; }
    }

    public sealed class CharacterAppearanceOffer
    {
        public CharacterAppearanceOffer(string kind, string slot, IReadOnlyList<string> options)
        {
            Kind = kind;
            Slot = slot;
            Options = options;
        }

        public string Kind { get; }

        public string Slot { get; }

        public IReadOnlyList<string> Options { get; }
    }

    // The validated content of character looks.
    public sealed class CharacterAppearanceCatalog
    {
        private readonly Dictionary<string, CharacterAppearanceKind> kinds;
        private readonly Dictionary<string, CharacterAppearanceOffer[]> offers;

        private CharacterAppearanceCatalog(
            Dictionary<string, CharacterAppearanceKind> kinds,
            Dictionary<string, CharacterAppearanceOffer[]> offers,
            string[] bodyModels,
            string[] visibleEquipmentSlots)
        {
            this.kinds = kinds;
            this.offers = offers;
            BodyModels = bodyModels;
            VisibleEquipmentSlots = visibleEquipmentSlots;
        }

        // In content order; the first is the default.
        public IReadOnlyList<string> BodyModels { get; }

        public string DefaultBodyModel
        {
            get { return BodyModels[0]; }
        }

        // The equipment slots whose items other players see on a character.
        public IReadOnlyList<string> VisibleEquipmentSlots { get; }

        public IReadOnlyCollection<CharacterAppearanceKind> Kinds
        {
            get { return kinds.Values; }
        }

        public bool TryGetKind(string id, out CharacterAppearanceKind kind)
        {
            return kinds.TryGetValue(id ?? string.Empty, out kind);
        }

        public bool HasBodyModel(string bodyModel)
        {
            return bodyModel != null && offers.ContainsKey(bodyModel);
        }

        // What a body model offers per kind and slot, in content order, or none for an unknown model.
        public IReadOnlyList<CharacterAppearanceOffer> OffersFor(string bodyModel)
        {
            return bodyModel != null && offers.TryGetValue(bodyModel, out var found)
                ? found
                : Array.Empty<CharacterAppearanceOffer>();
        }

        // The look a body model has when nothing is chosen: the first option of every required kind
        // and slot it offers, and none of the others.
        public IReadOnlyList<CharacterAppearanceChoice> DefaultChoices(string bodyModel)
        {
            return OffersFor(bodyModel)
                .Where(offer => kinds[offer.Kind].Required)
                .Select(offer => new CharacterAppearanceChoice(offer.Kind, offer.Slot, offer.Options[0]))
                .ToArray();
        }

        // Why a look cannot be chosen in character creation, or null: the body model must be known,
        // every choice an option it offers, at most one per kind and slot, and every required kind and
        // slot it offers chosen.
        public string CreationError(string bodyModel, IReadOnlyList<CharacterAppearanceChoice> choices)
        {
            if (!HasBodyModel(bodyModel))
            {
                return "The body model '" + bodyModel + "' does not exist.";
            }

            var error = ShapeError(choices);
            if (error != null)
            {
                return error;
            }

            var offered = OffersFor(bodyModel);
            foreach (var choice in choices)
            {
                var offer = offered.FirstOrDefault(candidate => candidate.Kind == choice.Kind && candidate.Slot == choice.Slot);
                if (offer == null || !offer.Options.Contains(choice.Option))
                {
                    return "The body model '" + bodyModel + "' does not offer " + choice + ".";
                }
            }

            foreach (var offer in offered.Where(candidate => kinds[candidate.Kind].Required))
            {
                if (!choices.Any(choice => choice.Kind == offer.Kind && choice.Slot == offer.Slot))
                {
                    return "The look needs a " + offer.Kind + (offer.Slot.Length > 0 ? " in " + offer.Slot : string.Empty) + ".";
                }
            }

            return null;
        }

        // Why a look cannot be stored, or null: the body model must be known, and every choice an
        // option that some body model offers for its kind and slot, at most one per kind and slot.
        // One look serves every body, so a choice the body model lacks, such as a face of another
        // body, is kept, and the body shows its own default there.
        public string StorageError(string bodyModel, IReadOnlyList<CharacterAppearanceChoice> choices)
        {
            if (!HasBodyModel(bodyModel))
            {
                return "The body model '" + bodyModel + "' does not exist.";
            }

            var error = ShapeError(choices);
            if (error != null)
            {
                return error;
            }

            foreach (var choice in choices)
            {
                if (!offers.Values.Any(found => found.Any(offer =>
                        offer.Kind == choice.Kind && offer.Slot == choice.Slot && offer.Options.Contains(choice.Option))))
                {
                    return "No body model offers " + choice + ".";
                }
            }

            return null;
        }

        private string ShapeError(IReadOnlyList<CharacterAppearanceChoice> choices)
        {
            if (choices == null)
            {
                return "The look has no choices.";
            }

            if (choices.Count > CharacterAppearanceFormat.MaximumOffers)
            {
                return "The look has more than " + CharacterAppearanceFormat.MaximumOffers + " choices.";
            }

            foreach (var choice in choices)
            {
                if (!kinds.ContainsKey(choice.Kind))
                {
                    return "The look kind '" + choice.Kind + "' does not exist.";
                }
            }

            var duplicate = choices.GroupBy(choice => (choice.Kind, choice.Slot)).FirstOrDefault(group => group.Count() > 1);
            return duplicate != null
                ? "The look chooses more than one " + duplicate.Key.Kind + (duplicate.Key.Slot.Length > 0 ? " in " + duplicate.Key.Slot : string.Empty) + "."
                : null;
        }

        // Validates the content and builds the catalog, or throws with the first broken rule.
        public static CharacterAppearanceCatalog Create(CharacterAppearanceDocument document)
        {
            if (document == null)
            {
                throw new CharacterAppearanceValidationException("The appearance content is missing.");
            }

            if (document.formatVersion != CharacterAppearanceFormat.Version)
            {
                throw new CharacterAppearanceValidationException(
                    "formatVersion must be " + CharacterAppearanceFormat.Version + ".");
            }

            var kindDocuments = document.kinds ?? new List<CharacterAppearanceKindDocument>();
            if (kindDocuments.Count == 0 || kindDocuments.Count > CharacterAppearanceFormat.MaximumKinds)
            {
                throw new CharacterAppearanceValidationException(
                    "kinds must list 1 to " + CharacterAppearanceFormat.MaximumKinds + " kinds.");
            }

            var kinds = new Dictionary<string, CharacterAppearanceKind>(StringComparer.Ordinal);
            foreach (var kind in kindDocuments)
            {
                if (kind == null || !CharacterAppearanceFormat.KnownKinds.Contains(kind.id))
                {
                    throw new CharacterAppearanceValidationException(
                        "Every kind must be one of " + string.Join(", ", CharacterAppearanceFormat.KnownKinds) + ".");
                }

                if (kinds.ContainsKey(kind.id))
                {
                    throw new CharacterAppearanceValidationException("The kind '" + kind.id + "' is listed twice.");
                }

                kinds.Add(kind.id, new CharacterAppearanceKind(kind.id, kind.required, kind.changesInGame));
            }

            var bodyDocuments = document.bodyModels ?? new List<CharacterBodyModelDocument>();
            if (bodyDocuments.Count == 0 || bodyDocuments.Count > CharacterAppearanceFormat.MaximumBodyModels)
            {
                throw new CharacterAppearanceValidationException(
                    "bodyModels must list 1 to " + CharacterAppearanceFormat.MaximumBodyModels + " body models.");
            }

            var offers = new Dictionary<string, CharacterAppearanceOffer[]>(StringComparer.Ordinal);
            var bodyModels = new List<string>();
            foreach (var body in bodyDocuments)
            {
                if (body == null || !IsName(body.id, CharacterAppearanceFormat.MaximumNameLength, true))
                {
                    throw new CharacterAppearanceValidationException(
                        "Every body model needs an id of letters, digits, and underscores.");
                }

                if (offers.ContainsKey(body.id))
                {
                    throw new CharacterAppearanceValidationException("The body model '" + body.id + "' is listed twice.");
                }

                offers.Add(body.id, CreateOffers(body, kinds));
                bodyModels.Add(body.id);
            }

            var visible = (document.visibleEquipmentSlots ?? new List<string>()).ToArray();
            if (visible.Length > CharacterAppearanceFormat.MaximumVisibleEquipmentSlots
                || visible.Any(slot => !IsIdentifier(slot, CharacterAppearanceFormat.MaximumSlotLength))
                || visible.Distinct(StringComparer.Ordinal).Count() != visible.Length)
            {
                throw new CharacterAppearanceValidationException(
                    "visibleEquipmentSlots must list at most " + CharacterAppearanceFormat.MaximumVisibleEquipmentSlots
                    + " equipment slot ids, each once.");
            }

            return new CharacterAppearanceCatalog(kinds, offers, bodyModels.ToArray(), visible);
        }

        private static CharacterAppearanceOffer[] CreateOffers(
            CharacterBodyModelDocument body,
            Dictionary<string, CharacterAppearanceKind> kinds)
        {
            var documents = body.offers ?? new List<CharacterAppearanceOfferDocument>();
            if (documents.Count > CharacterAppearanceFormat.MaximumOffers)
            {
                throw new CharacterAppearanceValidationException(
                    body.id + " offers more than " + CharacterAppearanceFormat.MaximumOffers + " kinds and slots.");
            }

            var result = new List<CharacterAppearanceOffer>();
            foreach (var offer in documents)
            {
                var where = body.id + " " + (offer != null ? offer.kind : string.Empty);
                if (offer == null || !kinds.ContainsKey(offer.kind ?? string.Empty))
                {
                    throw new CharacterAppearanceValidationException(where + ": every offer names a listed kind.");
                }

                var slot = offer.slot ?? string.Empty;
                if (slot.Length > 0 && !IsIdentifier(slot, CharacterAppearanceFormat.MaximumSlotLength))
                {
                    throw new CharacterAppearanceValidationException(where + ": the slot must be a lowercase identifier or empty.");
                }

                if (result.Any(found => found.Kind == offer.kind && found.Slot == slot))
                {
                    throw new CharacterAppearanceValidationException(where + ": a kind and slot is offered twice.");
                }

                var options = (offer.options ?? new List<string>()).ToArray();
                if (options.Length == 0 || options.Length > CharacterAppearanceFormat.MaximumOptions
                    || options.Any(option => !IsName(option, CharacterAppearanceFormat.MaximumNameLength, false))
                    || options.Distinct(StringComparer.Ordinal).Count() != options.Length)
                {
                    throw new CharacterAppearanceValidationException(
                        where + ": options must list 1 to " + CharacterAppearanceFormat.MaximumOptions
                        + " names of letters and digits, each once.");
                }

                result.Add(new CharacterAppearanceOffer(offer.kind, slot, options));
            }

            return result.ToArray();
        }

        // Option names are letters and digits, as the appearance catalogs of the bodies name them;
        // body model ids may also hold underscores, such as Human_Male.
        private static bool IsName(string text, int maximumLength, bool underscores)
        {
            return !string.IsNullOrEmpty(text) && text.Length <= maximumLength && char.IsLetter(text[0])
                && text.All(character => character < 128 && (char.IsLetterOrDigit(character) || (underscores && character == '_')));
        }

        private static bool IsIdentifier(string text, int maximumLength)
        {
            return !string.IsNullOrEmpty(text) && text.Length <= maximumLength && text[0] >= 'a' && text[0] <= 'z'
                && text.All(character => (character >= 'a' && character <= 'z') || (character >= '0' && character <= '9') || character == '_');
        }
    }
}
