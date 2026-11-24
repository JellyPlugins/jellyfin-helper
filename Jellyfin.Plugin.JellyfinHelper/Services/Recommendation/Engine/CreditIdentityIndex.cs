using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Canonical person keys and conflict-aware exact-name legacy aliases for one metadata boundary.</summary>
internal sealed class CreditIdentityIndex
{
    private readonly Dictionary<string, string> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _conflicts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="CreditIdentityIndex"/> class before attaching legacy name aliases.</summary>
    /// <param name="people">Only metadata permitted at this scoring/prediction boundary.</param>
    internal CreditIdentityIndex(IEnumerable<PersonCredit> people)
    {
        var records = people.ToArray();
        foreach (var person in records)
        {
            Register(person);
        }

        foreach (var component in _roots.Keys.GroupBy(Root, StringComparer.OrdinalIgnoreCase).Where(component => component.GroupBy(key => key[..key.LastIndexOf(':')], StringComparer.OrdinalIgnoreCase).Any(provider => provider.Skip(1).Any())))
        {
            _conflicts.Add(component.Key);
        }

        foreach (var person in records.Where(person => !string.IsNullOrWhiteSpace(person.Name)))
        {
            if (!_names.TryGetValue(person.Name, out var identities))
            {
                identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _names.Add(person.Name, identities);
            }

            identities.UnionWith(Identities(person).Select(Root));
        }
    }

    private void Register(PersonCredit person)
    {
        var identities = Identities(person);
        foreach (var key in identities)
        {
            _roots.TryAdd(key, key);
        }

        if (ConflictingProviders(person))
        {
            return;
        }

        foreach (var key in identities.Skip(1))
        {
            var first = Root(identities[0]);
            var next = Root(key);
            var canonical = string.Compare(first, next, StringComparison.OrdinalIgnoreCase) <= 0 ? first : next;
            _roots[first] = canonical;
            _roots[next] = canonical;
        }
    }

    private string Root(string key)
    {
        var parent = _roots.GetValueOrDefault(key, key);
        if (string.Equals(parent, key, StringComparison.OrdinalIgnoreCase))
        {
            return key;
        }

        var root = Root(parent);
        _roots[key] = root;
        return root;
    }

    /// <summary>Returns a typed identity, or an unambiguous exact-name legacy key; conflicts are unavailable.</summary>
    /// <param name="person">An observed person credit.</param>
    /// <returns>A match key that can never equal a title/company/network key.</returns>
    internal string? Key(PersonCredit person)
    {
        if (ConflictingProviders(person))
        {
            return null;
        }

        var known = Identities(person);
        if (known.Length > 0)
        {
            var root = Root(known[0]);
            return _conflicts.Contains(root) ? null : root;
        }

        if (string.IsNullOrWhiteSpace(person.Name))
        {
            return null;
        }

        if (_names.TryGetValue(person.Name, out var ids) && ids.Count > 0)
        {
            return ids.Count == 1 && !_conflicts.Contains(ids.First()) ? ids.First() : null;
        }

        return "person:name:" + person.Name;
    }

    private static string[] Identities(PersonCredit person)
        => (person.Identities.IsDefault ? [] : person.Identities).Select(identity => CreditMetadataCapture.Normalize(identity.Provider, identity.Id))
            .OfType<ProviderPersonIdentity>().Select(identity => "person:" + identity.Provider + ":" + identity.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool ConflictingProviders(PersonCredit person)
        => (person.Identities.IsDefault ? [] : person.Identities).Select(identity => CreditMetadataCapture.Normalize(identity.Provider, identity.Id))
            .OfType<ProviderPersonIdentity>().GroupBy(identity => identity.Provider, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Select(identity => identity.Id).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any());
}
