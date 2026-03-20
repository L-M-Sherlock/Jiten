import { defineStore } from 'pinia';
import type { StudyDeckDto, StudyBatchResponse, StudyCardDto, StudySettingsDto, AddStudyDeckRequest, UpdateStudyDeckRequest, DueSummaryDto, DeckStreakDto } from '~/types';
import { FsrsRating } from '~/types';

interface SessionReview {
  wordId: number;
  readingIndex: number;
  wordText: string;
  reading: string;
  rating: FsrsRating;
  duration: number | undefined;
}

export interface HardestCard {
  wordId: number;
  readingIndex: number;
  wordText: string;
  reading: string;
  againCount: number;
  reviewCount: number;
  avgDuration: number;
}

interface UndoSnapshot {
  card: StudyCardDto;
  type: 'grade' | 'blacklist' | 'master' | 'forget' | 'suspend';
  rating?: FsrsRating;
  batch: StudyCardDto[];
  cardIndex: number;
  againKeys: Set<string>;
  grades: ('hard' | 'good' | 'easy' | 'action')[];
  reviews: SessionReview[];
  stats: {
    cardsReviewed: number;
    newCardsLearned: number;
    correctCount: number;
    startTime: Date | null;
    gradeCounts: { again: number; hard: number; good: number; easy: number };
  };
}

export const useSrsStore = defineStore('srs', () => {
  const { $api } = useNuxtApp();

  const studyDecks = ref<StudyDeckDto[]>([]);
  const sessionId = ref<string | null>(null);
  const currentBatch = ref<StudyCardDto[]>([]);
  const currentCardIndex = ref(0);
  const isFlipped = ref(false);
  const isLoading = ref(false);
  const isSessionComplete = ref(false);
  const isWrappingUp = ref(false);
  const preWrapUpBatch = ref<StudyCardDto[]>([]);
  const studySettings = ref<StudySettingsDto>({
    newCardsPerDay: 20,
    maxReviewsPerDay: 200,
    gradingButtons: 4,
    interleaving: 'Mixed',
    newCardOrder: 'DeckFrequency',
    reviewFrom: 'AllTracked',
    showPitchAccent: true,
    exampleSentencePosition: 'Back',
    showFrequencyRank: true,
    showKanjiBreakdown: true,
    showNextInterval: false,
    showKeybinds: true,
    showElapsedTime: true,
  });
  const sessionStats = ref({
    cardsReviewed: 0,
    newCardsLearned: 0,
    correctCount: 0,
    startTime: null as Date | null,
    gradeCounts: { again: 0, hard: 0, good: 0, easy: 0 },
  });
  const newCardsRemaining = ref(0);
  const reviewsRemaining = ref(0);
  const newCardsToday = ref(0);
  const reviewsToday = ref(0);
  const againCardKeys = ref(new Set<string>());
  const clearedGrades = ref<('hard' | 'good' | 'easy' | 'action')[]>([]);
  const undoState = ref<UndoSnapshot | null>(null);
  const sessionReviews = ref<SessionReview[]>([]);
  const cardShownAt = ref<number | null>(null);
  const thinkingDuration = ref<number | undefined>(undefined);
  const isBusy = ref(false);
  const fetchError = ref<string | null>(null);
  const dueSummary = ref<DueSummaryDto | null>(null);
  const deckStreak = ref<DeckStreakDto | null>(null);

  const canUndo = computed(() => undoState.value !== null);

  const currentCard = computed(() =>
    currentCardIndex.value < currentBatch.value.length
      ? currentBatch.value[currentCardIndex.value]
      : null
  );

  const hasCards = computed(() => currentBatch.value.length > 0 && currentCardIndex.value < currentBatch.value.length);

  const progress = computed(() => ({
    current: Math.min(currentCardIndex.value + 1, currentBatch.value.length),
    total: currentBatch.value.length,
    reviewed: sessionStats.value.cardsReviewed,
    remaining: currentBatch.value.length - currentCardIndex.value,
  }));

  const remainingByType = computed(() => {
    let newCount = 0;
    let reviewCount = 0;
    for (let i = currentCardIndex.value; i < currentBatch.value.length; i++) {
      if (currentBatch.value[i].isNewCard) newCount++;
      else reviewCount++;
    }
    return { new: newCount, review: reviewCount };
  });

  const againCardsAhead = computed(() => {
    let count = 0;
    for (let i = currentCardIndex.value; i < currentBatch.value.length; i++) {
      const c = currentBatch.value[i];
      if (againCardKeys.value.has(`${c.wordId}-${c.readingIndex}`)) count++;
    }
    return count;
  });

  const hardestCards = computed<HardestCard[]>(() => {
    const grouped = new Map<string, HardestCard>();
    for (const r of sessionReviews.value) {
      const key = `${r.wordId}-${r.readingIndex}`;
      const existing = grouped.get(key);
      if (existing) {
        if (r.rating === FsrsRating.Again) existing.againCount++;
        if (r.duration) existing.avgDuration += r.duration;
        existing.reviewCount++;
      } else {
        grouped.set(key, {
          wordId: r.wordId,
          readingIndex: r.readingIndex,
          wordText: r.wordText,
          reading: r.reading,
          againCount: r.rating === FsrsRating.Again ? 1 : 0,
          reviewCount: 1,
          avgDuration: r.duration ?? 0,
        });
      }
    }
    for (const card of grouped.values()) {
      if (card.reviewCount > 0) card.avgDuration = Math.round(card.avgDuration / card.reviewCount);
    }
    return [...grouped.values()]
      .filter(c => c.againCount > 0)
      .sort((a, b) => b.againCount - a.againCount || b.avgDuration - a.avgDuration)
      .slice(0, 5);
  });

  function takeSnapshot(card: StudyCardDto, type: UndoSnapshot['type'], rating?: FsrsRating) {
    undoState.value = {
      card,
      type,
      rating,
      batch: [...currentBatch.value],
      cardIndex: currentCardIndex.value,
      againKeys: new Set(againCardKeys.value),
      grades: [...clearedGrades.value],
      reviews: [...sessionReviews.value],
      stats: JSON.parse(JSON.stringify(sessionStats.value)),
    };
  }

  async function fetchStudyDecks() {
    try {
      studyDecks.value = await $api<StudyDeckDto[]>('srs/study-decks');
      fetchError.value = null;
    } catch (error) {
      console.error('Failed to fetch study decks:', error);
      fetchError.value = 'Failed to load study decks. Please try again.';
    }
  }

  async function fetchDueSummary() {
    try {
      dueSummary.value = await $api<DueSummaryDto>('srs/due-summary');
    } catch {
      dueSummary.value = null;
    }
  }

  async function fetchDeckStreak() {
    try {
      deckStreak.value = await $api<DeckStreakDto>('srs/deck-streak');
    } catch {
      deckStreak.value = null;
    }
  }

  async function addStudyDeck(request: AddStudyDeckRequest) {
    const result = await $api<{ userStudyDeckId: number }>('srs/study-decks', {
      method: 'POST',
      body: request,
    });
    await fetchStudyDecks();
    return result;
  }

  async function updateStudyDeck(id: number, request: UpdateStudyDeckRequest) {
    await $api(`srs/study-decks/${id}`, {
      method: 'PUT',
      body: request,
    });
    await fetchStudyDecks();
  }

  async function removeStudyDeck(id: number) {
    await $api(`srs/study-decks/${id}`, { method: 'DELETE' });
    studyDecks.value = studyDecks.value.filter(d => d.userStudyDeckId !== id);
  }

  async function reorderStudyDecks(reorderedDecks: StudyDeckDto[]) {
    studyDecks.value = reorderedDecks;
    await $api('srs/study-decks/reorder', {
      method: 'PUT',
      body: {
        items: reorderedDecks.map((d, i) => ({
          userStudyDeckId: d.userStudyDeckId,
          sortOrder: i,
        })),
      },
    });
  }

  async function fetchBatch(limit = 20) {
    if (isWrappingUp.value) return;

    isLoading.value = true;
    const isRefetch = sessionStats.value.cardsReviewed > 0;
    try {
      const params = new URLSearchParams({ limit: String(limit) });
      if (sessionId.value) params.append('sessionId', sessionId.value);
      const response = await $api<StudyBatchResponse>(`srs/study-batch?${params}`);
      sessionId.value = response.sessionId;
      if (isRefetch && response.cards.length > 0) {
        currentBatch.value = [...currentBatch.value, ...response.cards];
      } else if (isRefetch && response.cards.length === 0) {
        isSessionComplete.value = true;
      } else {
        currentBatch.value = response.cards;
        currentCardIndex.value = 0;
        isSessionComplete.value = false;
      }
      isFlipped.value = false;
      cardShownAt.value = Date.now();
      newCardsRemaining.value = response.newCardsRemaining;
      reviewsRemaining.value = response.reviewsRemaining;
      newCardsToday.value = response.newCardsToday;
      reviewsToday.value = response.reviewsToday;
      if (!sessionStats.value.startTime) {
        sessionStats.value.startTime = new Date();
      }
      fetchError.value = null;
    } catch (error) {
      console.error('Failed to fetch study batch:', error);
      fetchError.value = 'Failed to load cards. Please try again.';
    } finally {
      isLoading.value = false;
    }
  }

  function revealCard() {
    isFlipped.value = true;
    thinkingDuration.value = cardShownAt.value ? Date.now() - cardShownAt.value : undefined;
  }

  async function gradeCard(rating: FsrsRating): Promise<boolean> {
    const card = currentCard.value;
    if (!card || isBusy.value) return true;
    isBusy.value = true;

    try {
      takeSnapshot(card, 'grade', rating);

      const AFK_THRESHOLD = 60_000;
      const reviewDuration = thinkingDuration.value !== undefined
        ? Math.min(thinkingDuration.value, AFK_THRESHOLD) : undefined;

      const clientRequestId = crypto.randomUUID();
      const body = {
        wordId: card.wordId,
        readingIndex: card.readingIndex,
        rating,
        reviewDuration,
        sessionId: sessionId.value,
        clientRequestId,
      };

      try {
        await $api('srs/review', { method: 'POST', body });
      } catch (firstError: any) {
        if (firstError?.status !== 429) {
          try {
            await $api('srs/review', { method: 'POST', body });
          } catch (retryError: any) {
            if (retryError?.status !== 429) throw retryError;
          }
        }
      }

      const kanaReading = card.readings.find(r => r.formType === 1)?.text ?? card.wordTextPlain;
      sessionReviews.value = [...sessionReviews.value, {
        wordId: card.wordId,
        readingIndex: card.readingIndex,
        wordText: card.wordTextPlain,
        reading: kanaReading,
        rating,
        duration: reviewDuration,
      }];

      const cardKey = `${card.wordId}-${card.readingIndex}`;
      const isRepeat = againCardKeys.value.has(cardKey);

      sessionStats.value.cardsReviewed++;
      if (card.isNewCard && !isRepeat) sessionStats.value.newCardsLearned++;
      if (rating >= FsrsRating.Good) sessionStats.value.correctCount++;
      if (rating === FsrsRating.Again) sessionStats.value.gradeCounts.again++;
      else if (rating === FsrsRating.Hard) sessionStats.value.gradeCounts.hard++;
      else if (rating === FsrsRating.Good) sessionStats.value.gradeCounts.good++;
      else if (rating === FsrsRating.Easy) sessionStats.value.gradeCounts.easy++;

      if (rating === FsrsRating.Again) {
        const newSet = new Set(againCardKeys.value);
        newSet.add(cardKey);
        againCardKeys.value = newSet;

        const batch = [...currentBatch.value];
        batch.splice(currentCardIndex.value, 1);
        const remaining = batch.length - currentCardIndex.value;
        const offset = remaining <= 0 ? 0 : Math.min(Math.floor(Math.random() * 6) + 5, remaining);
        batch.splice(currentCardIndex.value + offset, 0, { ...card });
        currentBatch.value = batch;
      } else {
        if (isRepeat) {
          const newSet = new Set(againCardKeys.value);
          newSet.delete(cardKey);
          againCardKeys.value = newSet;
        }
        const grade = rating === FsrsRating.Hard ? 'hard' : rating === FsrsRating.Easy ? 'easy' : 'good';
        clearedGrades.value = [...clearedGrades.value, grade];
        currentCardIndex.value++;
      }

      isFlipped.value = false;
      cardShownAt.value = Date.now();

      if (currentCardIndex.value >= currentBatch.value.length) {
        if (isWrappingUp.value) {
          isSessionComplete.value = true;
        } else {
          await fetchBatch();
        }
      }
      return true;
    } catch (error) {
      undoState.value = null;
      console.error('Failed to grade card:', error);
      return false;
    } finally {
      isBusy.value = false;
    }
  }

  async function quickAction(action: 'blacklist' | 'master' | 'forget' | 'suspend'): Promise<boolean> {
    const card = currentCard.value;
    if (!card || isBusy.value) return true;
    isBusy.value = true;

    const stateMap: Record<string, string> = {
      blacklist: 'blacklist-add',
      master: 'neverForget-add',
      forget: 'forget-add',
      suspend: 'suspend-add',
    };

    try {
      if (action !== 'forget') takeSnapshot(card, action);
      else undoState.value = null;

      await $api('srs/set-vocabulary-state', {
        method: 'POST',
        body: {
          wordId: card.wordId,
          readingIndex: card.readingIndex,
          state: stateMap[action],
        },
      });

      sessionStats.value.cardsReviewed++;
      clearedGrades.value = [...clearedGrades.value, 'action'];
      currentCardIndex.value++;
      isFlipped.value = false;
      cardShownAt.value = Date.now();

      if (currentCardIndex.value >= currentBatch.value.length) {
        if (isWrappingUp.value) {
          isSessionComplete.value = true;
        } else {
          await fetchBatch();
        }
      }
    } catch (error) {
      undoState.value = null;
      console.error('Failed to set vocabulary state:', error);
      return false;
    } finally {
      isBusy.value = false;
    }
    return true;
  }

  async function undoLastAction(): Promise<boolean> {
    const snap = undoState.value;
    if (!snap || isBusy.value) return true;
    isBusy.value = true;

    try {
      if (snap.type === 'grade') {
        await $api('srs/undo-review', {
          method: 'POST',
          body: { wordId: snap.card.wordId, readingIndex: snap.card.readingIndex },
        });
      } else {
        const revertState = snap.type === 'blacklist' ? 'blacklist-remove' : snap.type === 'suspend' ? 'suspend-remove' : 'neverForget-remove';
        await $api('srs/set-vocabulary-state', {
          method: 'POST',
          body: {
            wordId: snap.card.wordId,
            readingIndex: snap.card.readingIndex,
            state: revertState,
          },
        });
      }

      currentBatch.value = snap.batch;
      currentCardIndex.value = snap.cardIndex;
      againCardKeys.value = new Set(snap.againKeys);
      clearedGrades.value = [...snap.grades];
      sessionReviews.value = [...snap.reviews];
      sessionStats.value = {
        ...JSON.parse(JSON.stringify(snap.stats)),
        startTime: snap.stats.startTime ? new Date(snap.stats.startTime) : null,
      };
      isFlipped.value = true;
      cardShownAt.value = Date.now();
      isSessionComplete.value = false;
      undoState.value = null;
      return true;
    } catch (error) {
      console.error('Failed to undo:', error);
      return false;
    } finally {
      isBusy.value = false;
    }
  }

  function wrapUp() {
    if (isWrappingUp.value) return;
    isWrappingUp.value = true;
    preWrapUpBatch.value = [...currentBatch.value];

    // Keep the current card + any "again" cards still in the queue
    const upcoming = currentBatch.value.slice(currentCardIndex.value + 1);
    const keptAgain = upcoming.filter((c) => {
      const key = `${c.wordId}-${c.readingIndex}`;
      return againCardKeys.value.has(key);
    });

    const current = currentBatch.value[currentCardIndex.value];
    currentBatch.value = [
      ...currentBatch.value.slice(0, currentCardIndex.value),
      ...(current ? [current] : []),
      ...keptAgain,
    ];

    if (currentCardIndex.value >= currentBatch.value.length) {
      isSessionComplete.value = true;
    }
  }

  function cancelWrapUp() {
    if (!isWrappingUp.value) return;
    isWrappingUp.value = false;
    currentBatch.value = preWrapUpBatch.value;
    preWrapUpBatch.value = [];
  }

  async function fetchSettings() {
    try {
      studySettings.value = await $api<StudySettingsDto>('srs/study-settings');
    } catch {
      // Use defaults
    }
  }

  async function updateSettings(settings: StudySettingsDto) {
    studySettings.value = await $api<StudySettingsDto>('srs/study-settings', {
      method: 'PUT',
      body: settings,
    });
  }

  function resetSession() {
    sessionId.value = null;
    currentBatch.value = [];
    currentCardIndex.value = 0;
    isFlipped.value = false;
    isSessionComplete.value = false;
    isWrappingUp.value = false;
    preWrapUpBatch.value = [];
    againCardKeys.value = new Set();
    clearedGrades.value = [];
    sessionReviews.value = [];
    undoState.value = null;
    isBusy.value = false;
    fetchError.value = null;
    sessionStats.value = {
      cardsReviewed: 0,
      newCardsLearned: 0,
      correctCount: 0,
      startTime: null,
      gradeCounts: { again: 0, hard: 0, good: 0, easy: 0 },
    };
  }

  return {
    studyDecks,
    currentBatch,
    currentCardIndex,
    isFlipped,
    isLoading,
    isSessionComplete,
    isWrappingUp,
    studySettings,
    sessionStats,
    newCardsRemaining,
    reviewsRemaining,
    newCardsToday,
    reviewsToday,
    againCardsAhead,
    remainingByType,
    clearedGrades,
    hardestCards,
    isBusy,
    fetchError,
    dueSummary,
    deckStreak,
    canUndo,
    currentCard,
    hasCards,
    progress,
    fetchStudyDecks,
    fetchDueSummary,
    fetchDeckStreak,
    addStudyDeck,
    updateStudyDeck,
    removeStudyDeck,
    reorderStudyDecks,
    fetchBatch,
    revealCard,
    gradeCard,
    quickAction,
    undoLastAction,
    wrapUp,
    againCardKeys,
    cancelWrapUp,
    fetchSettings,
    updateSettings,
    resetSession,
  };
});
