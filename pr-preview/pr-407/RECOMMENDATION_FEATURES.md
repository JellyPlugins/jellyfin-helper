# Recommendation model v6

Heuristic, Learned and Neural use **one 41-feature catalogue**. Learned persists 41 coefficients; Neural uses **41 → 82 → 96 → 48 → 24 → 1**, with ReLU hidden layers and a sigmoid output (17,369 trainable parameters).

Model parameter files must declare version 6 and match these exact dimensions. Unsupported files are not converted or erased while loading. New fitting and independent admission are required before new parameters become serving models. Model-version changes never reset title-reuse ledgers or manufacture independent feedback.

## Feature list

Indices and names match `FeatureIndex` and the read-only diagnostics catalogue. All coordinates are bounded to [0,1] before model normalization.

| Index | Feature | Meaning |
| --- | --- | --- |
| 0 | `GenreSimilarity` | Personal genre affinity, including bounded coherent-content refinement. |
| 1 | `CollaborativeScore` | Self-excluded neighbour co-occurrence. |
| 2 | `CombinedCriticScore` | Provider critic/community-rating blend. |
| 3 | `RecencyScore` | Release recency at prediction time. |
| 4 | `YearProximityScore` | Proximity to the personal watched-year distribution. |
| 5 | `GenreCountNormalized` | Genre count divided by its fixed ceiling and clamped. |
| 6 | `IsSeries` | Television-versus-movie indicator. |
| 7 | `GenreCriticInteraction` | Genre affinity × critic score. |
| 8 | `GenreCollabInteraction` | Genre affinity × collaborative score. |
| 9 | `UserRatingScore` | Self-excluded genre-level explicit-rating aggregate. |
| 10 | `CompletionRatio` | Self-excluded genre-average playback completion; not candidate playback. |
| 11 | `IsAbandoned` | Genre-level abandonment rate; not candidate rejection. |
| 12 | `HasInteraction` | Genre familiarity; not candidate interaction evidence. |
| 13 | `PeopleSimilarity` | Preferred cast/director overlap. |
| 14 | `StudioMatch` | Personal studio match. |
| 15 | `SeriesAffinity` | Affinity to progressing series; the legacy property alias is not another coordinate. |
| 16 | `PopularityScore` | Local collaborative/critic proxy or external provider popularity. |
| 17 | `DayOfWeekAffinity` | Historical genre affinity in the prediction-day bucket. |
| 18 | `HourOfDayAffinity` | Historical genre affinity in the prediction-hour bucket. |
| 19 | `IsWeekend` | Prediction-time weekend indicator. |
| 20 | `TagSimilarity` | Raw tag-Jaccard; never silently replaced with title-level semantic affinity. |
| 21 | `PeopleGenreInteraction` | People similarity × genre affinity. |
| 22 | `RecencyCriticInteraction` | Release recency × critic score. |
| 23 | `GenreUnderexposure` | Underrepresented personal genres. |
| 24 | `GenreDominanceRatio` | Dominance of already-common personal genres. |
| 25 | `GenreAffinityGap` | Personal affinity/exposure gap. |
| 26 | `LibraryAddedRecency` | Library-addition recency at prediction time. |
| 27 | `ContentNearestNeighborScore` | Closest watched-title genre/people/studio match. |
| 28 | `LanguageAffinity` | Available audio languages versus evidenced personal playback languages. |
| 29 | `CollectionProgressionBoost` | Confirmed, self-excluded collection progress. |
| 30 | `SubtitleLanguageAffinity` | Available subtitles versus evidenced personal subtitle choices. |
| 31 | `FranchiseAffinity` | Stable provider collection identity, with conservative name fallback. |
| 32 | `ProductionLocationAffinity` | Personal production-country affinity; not audience classification. |
| 33 | `InheritedTagSimilarity` | Separately captured inherited-tag overlap. |
| 34 | `SeriesCompletability` | Known series lifecycle/completability. |
| 35 | `WriterAffinity` | Personal writer/creator affinity. |
| 36 | `BillingWeightedPeople` | Preferred collaborators weighted by candidate billing. |
| 37 | `GenreStudioIdfPrior` | Library-level genre/studio rarity. |
| 38 | `ContentFitAffinity` | Weighted own-keyword overlap with coherent positive title documents. |
| 39 | `ContentFitSupport` | Verified narrative matches and majority coverage of a positive title document, with overlapping genres. |
| 40 | `AudienceMismatch` | Bounded soft personal audience hint; never certification permission or a blanket age ban. |

## Evidence and inference

- Technical transport/language tags supply no thematic evidence. The complete genre/alias catalogue (including provider labels) is broad evidence, never specific story evidence; adaptation labels cannot manufacture narrative support.
- Known thematic nonmatches are real zeros. Unavailable language/theme inputs use each model's fixed **fitting-only** imputation means; validation, acceptance and future target activity never determine those means.
- Historical preferences use dated, strictly earlier, self-excluded playback. Saved indexes are not reconstructed earlier stream choices. Missing/partial language metadata and subtitles-off stay distinguishable from genuine preference.
- The heuristic budgets fine affinity at 0.08, genre affinity at 0.18, collection progress at 0.15 and languages at 0.045. Remaining positive priors share 0.545; total positive weight is 1.0, with separate negative penalties. Narrative confidence and audience mismatch have zero fixed raw coefficients because they are applied once in final score space. Learned and Neural learn all 41 coordinates; final confidence remains consistent across components and mixtures.
- Permission/certification checks are separate from taste scoring. Untouched suggestions are not independent negative evidence.
- Collection continuation stays neutral when known members are missing or cannot be placed in the scoped population; exclusions never import those titles into recommendations or training.
- Saved recommendation features and immutable predictors stay detached. Feedback uses the original prediction-time evidence, not later enrichment.

## Training state

Early-Stopping checkpoints restore both parameters and the matching Adam moments/clock. Neural persistence includes that complete continuing state. Dense input clamp envelopes move with normalization coordinates in both Learned and Neural, avoiding an unrelated projection after rebasing. Negative/nonfinite deviations are rejected; genuine zero/signed-zero constant features remain valid. Invalid optimizer metadata cannot poison otherwise valid inference.
