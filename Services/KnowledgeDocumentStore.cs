using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Project_no._1.Services;

public interface IKnowledgeDocumentStore
{
    bool TryAdd(string ownerId, string fileName, IReadOnlyList<DocumentPassage> passages, out string documentId);
    bool TryGet(string ownerId, string documentId, out StoredDocument? document);
    bool Remove(string ownerId, string documentId);
}

public sealed class KnowledgeDocumentStore : IKnowledgeDocumentStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private const int MaxDocuments = 20;
    private const int MaxStoredCharacters = 1_000_000;
    private readonly ConcurrentDictionary<string, StoredDocument> _documents = new();

    public bool TryAdd(string ownerId, string fileName, IReadOnlyList<DocumentPassage> passages, out string documentId)
    {
        lock (_documents)
        {
            RemoveExpired();
            var storedCharacters = _documents.Values.Sum(document => document.CharacterCount);
            var documentCharacters = passages.Sum(passage => passage.Text.Length);
            if (_documents.Count >= MaxDocuments || storedCharacters + documentCharacters > MaxStoredCharacters)
            {
                documentId = string.Empty;
                return false;
            }

            documentId = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            return _documents.TryAdd(documentId, new StoredDocument(
                ownerId,
                fileName,
                passages,
                documentCharacters,
                DateTimeOffset.UtcNow));
        }
    }

    public bool TryGet(string ownerId, string documentId, out StoredDocument? document)
    {
        if (_documents.TryGetValue(documentId, out document))
        {
            if (document.OwnerId == ownerId && DateTimeOffset.UtcNow - document.CreatedAt <= Lifetime)
            {
                return true;
            }

            if (DateTimeOffset.UtcNow - document.CreatedAt > Lifetime)
            {
                _documents.TryRemove(documentId, out _);
            }
        }

        document = null;
        return false;
    }

    public bool Remove(string ownerId, string documentId)
    {
        return _documents.TryGetValue(documentId, out var document) &&
            document.OwnerId == ownerId &&
            _documents.TryRemove(documentId, out _);
    }

    private void RemoveExpired()
    {
        var expiration = DateTimeOffset.UtcNow - Lifetime;
        foreach (var (id, document) in _documents)
        {
            if (document.CreatedAt < expiration)
            {
                _documents.TryRemove(id, out _);
            }
        }
    }
}

public sealed record DocumentPassage(int Number, int? Page, string Text);

public sealed record StoredDocument(
    string OwnerId,
    string FileName,
    IReadOnlyList<DocumentPassage> Passages,
    int CharacterCount,
    DateTimeOffset CreatedAt)
{
    public IReadOnlyList<DocumentPassage> FindRelevant(string query, int limit = 6)
    {
        var terms = Regex.Matches(query.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}")
            .Select(match => match.Value)
            .Distinct()
            .ToArray();

        if (terms.Length == 0)
        {
            return Passages.Take(limit).ToArray();
        }

        return Passages
            .Select(passage => new
            {
                Passage = passage,
                Score = terms.Sum(term => CountOccurrences(passage.Text, term))
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Passage.Number)
            .Take(limit)
            .Select(item => item.Passage)
            .ToArray();
    }

    private static int CountOccurrences(string text, string term)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(term, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            offset += term.Length;
        }

        return count;
    }
}
