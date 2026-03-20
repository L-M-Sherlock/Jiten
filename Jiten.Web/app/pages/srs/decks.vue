<script setup lang="ts">
  import { useSrsStore } from '~/stores/srsStore';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { MediaType, type StudyDeckDto } from '~/types';

  definePageMeta({ middleware: ['auth'] });
  useHead({ title: 'Study Decks' });

  const srsStore = useSrsStore();
  const toast = useToast();
  const confirm = useConfirm();
  const localiseTitle = useLocaliseTitle();
  const router = useRouter();

  const showAddDialog = ref(false);
  const editingDeck = ref<StudyDeckDto | undefined>(undefined);
  const showEditDialog = ref(false);
  const loading = ref(true);
  const deckListRef = ref<HTMLElement | null>(null);

  const refreshing = ref(false);

  onMounted(async () => {
    loading.value = true;
    await Promise.all([srsStore.fetchStudyDecks(), srsStore.fetchDueSummary(), srsStore.fetchDeckStreak()]);
    loading.value = false;
  });

  async function refresh() {
    refreshing.value = true;
    await Promise.all([srsStore.fetchStudyDecks(), srsStore.fetchDueSummary(), srsStore.fetchDeckStreak()]);
    refreshing.value = false;
  }

  function openEdit(deck: StudyDeckDto) {
    editingDeck.value = deck;
    showEditDialog.value = true;
  }

  function confirmRemove(id: number, title: string) {
    confirm.require({
      message: `Remove "${title}" from your study list? Your existing cards and progress will be kept.`,
      header: 'Remove Study Deck',
      acceptLabel: 'Remove',
      rejectLabel: 'Cancel',
      accept: async () => {
        try {
          await srsStore.removeStudyDeck(id);
          toast.add({ severity: 'info', summary: 'Deck removed', life: 2000 });
        }
        catch {
          toast.add({ severity: 'error', summary: 'Failed to remove deck', life: 3000 });
        }
      },
    });
  }

  function startStudy() {
    srsStore.resetSession();
    router.push('/srs/study');
  }

  function getCoverUrl(coverName?: string) {
    if (!coverName || coverName === 'nocover.jpg') return null;
    return coverName;
  }

  function pct(count: number, total: number) {
    return total > 0 ? ((count / total) * 100).toFixed(1) : '0.0';
  }

  function knownPct(deck: StudyDeckDto) {
    return pct(deck.masteredCount + deck.reviewCount, deck.totalWords);
  }

  function combinedPct(deck: StudyDeckDto) {
    return pct(deck.masteredCount + deck.reviewCount + deck.learningCount, deck.totalWords);
  }

  const { dragIndex, dropIndex, handlePointerDown } = useTouchReorder({
    containerRef: deckListRef,
    onReorder(from, to) {
      const decks = [...srsStore.studyDecks];
      const [moved] = decks.splice(from, 1);
      decks.splice(to, 0, moved);
      srsStore.reorderStudyDecks(decks);
    },
  });

  async function moveDeck(index: number, direction: -1 | 1) {
    const target = index + direction;
    if (target < 0 || target >= srsStore.studyDecks.length) return;
    const decks = [...srsStore.studyDecks];
    [decks[index], decks[target]] = [decks[target], decks[index]];
    await srsStore.reorderStudyDecks(decks);
  }

  const nextReviewText = computed(() => {
    const ds = srsStore.dueSummary;
    if (!ds?.nextReviewAt) return null;
    const next = new Date(ds.nextReviewAt);
    const diffMs = next.getTime() - Date.now();
    if (diffMs <= 0) return 'now';
    const diffMin = Math.floor(diffMs / 60000);
    if (diffMin < 60) return `${diffMin}m`;
    const diffHr = Math.floor(diffMin / 60);
    if (diffHr < 24) return `${diffHr}h ${diffMin % 60}m`;
    return `${Math.floor(diffHr / 24)}d ${diffHr % 24}h`;
  });

  const totalDue = computed(() => {
    const ds = srsStore.dueSummary;
    if (!ds) return 0;
    return ds.reviewsDue + ds.newCardsAvailable;
  });

  const CELL = 10;
  const GAP = 2;
  const WEEKS = 12;

  interface MiniDay {
    date: string;
    count: number;
    dow: number;
    weekIdx: number;
  }

  const miniHeatmap = computed<MiniDay[]>(() => {
    const ds = srsStore.deckStreak;
    if (!ds || ds.recentDays.length === 0) return [];

    const countMap = new Map<string, number>();
    for (const d of ds.recentDays) countMap.set(d.date, d.count);

    const today = new Date();
    today.setHours(0, 0, 0, 0);
    const todayDow = (today.getDay() + 6) % 7; // Mon=0
    const startOffset = (WEEKS - 1) * 7 + todayDow;

    const days: MiniDay[] = [];
    for (let i = startOffset; i >= 0; i--) {
      const d = new Date(today);
      d.setDate(d.getDate() - i);
      const dateStr = d.toISOString().slice(0, 10);
      const dow = (d.getDay() + 6) % 7;
      const weekIdx = Math.floor((startOffset - i) / 7);
      days.push({ date: dateStr, count: countMap.get(dateStr) ?? 0, dow, weekIdx });
    }
    return days;
  });

  const miniMaxCount = computed(() => {
    let max = 0;
    for (const d of miniHeatmap.value) {
      if (d.count > max) max = d.count;
    }
    return max || 1;
  });

  function miniIntensity(count: number): string {
    if (count <= 0) return 'bg-gray-100 dark:bg-gray-800';
    const ratio = count / miniMaxCount.value;
    if (ratio <= 0.25) return 'bg-purple-200 dark:bg-purple-900/60';
    if (ratio <= 0.5) return 'bg-purple-400 dark:bg-purple-700';
    if (ratio <= 0.75) return 'bg-purple-500 dark:bg-purple-500';
    return 'bg-purple-700 dark:bg-purple-400';
  }
</script>

<template>
  <div class="container mx-auto p-2 md:p-4">
    <div class="flex flex-wrap items-center justify-between gap-2 mb-6">
      <h2 class="text-2xl font-bold">Study Decks</h2>
      <div class="flex gap-2">
        <Button icon="pi pi-plus" label="Add Deck" class="!hidden sm:!inline-flex" @click="showAddDialog = true" />
        <Button icon="pi pi-plus" class="sm:!hidden" @click="showAddDialog = true" />
        <Button
          v-if="srsStore.studyDecks.length > 0"
          icon="pi pi-play"
          :label="totalDue > 0 ? `Study (${totalDue})` : 'No cards due'"
          :severity="totalDue > 0 ? 'success' : 'secondary'"
          :disabled="totalDue === 0"
          class="!hidden sm:!inline-flex"
          @click="startStudy"
        />
        <Button
          v-if="srsStore.studyDecks.length > 0"
          icon="pi pi-play"
          :badge="totalDue > 0 ? String(totalDue) : undefined"
          :severity="totalDue > 0 ? 'success' : 'secondary'"
          :disabled="totalDue === 0"
          class="sm:!hidden"
          @click="startStudy"
        />
        <Button icon="pi pi-refresh" severity="secondary" :loading="refreshing" @click="refresh" />
        <NuxtLink to="/settings/srs">
          <Button icon="pi pi-cog" class="sm:!hidden" severity="secondary" />
          <Button icon="pi pi-cog" label="Settings" severity="secondary" class="!hidden sm:!inline-flex" />
        </NuxtLink>
      </div>
    </div>

    <!-- Due Summary Banner -->
    <div
      v-if="!loading && srsStore.dueSummary && srsStore.studyDecks.length > 0"
      class="mb-6 rounded-xl border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-800 shadow-sm overflow-hidden"
    >
      <div class="grid grid-cols-2 md:grid-cols-4 divide-x divide-gray-200 dark:divide-gray-700">
        <button
          class="flex items-center justify-center gap-2 py-3 px-3 transition-colors hover:bg-gray-50 dark:hover:bg-gray-700/50 border-b md:border-b-0 border-gray-200 dark:border-gray-700"
          :class="srsStore.dueSummary.reviewsDue > 0 ? 'cursor-pointer' : 'opacity-50 cursor-default'"
          @click="srsStore.dueSummary!.reviewsDue > 0 && startStudy()"
        >
          <span
            class="text-2xl font-bold tabular-nums"
            :class="srsStore.dueSummary.reviewsDue > 0 ? 'text-blue-600 dark:text-blue-400' : 'text-gray-400 dark:text-gray-500'"
          >{{ srsStore.dueSummary.reviewsDue }}</span>
          <span class="text-xs text-gray-500 dark:text-gray-400">Reviews</span>
        </button>

        <button
          class="flex items-center justify-center gap-2 py-3 px-3 transition-colors hover:bg-gray-50 dark:hover:bg-gray-700/50 border-b md:border-b-0 border-gray-200 dark:border-gray-700"
          :class="srsStore.dueSummary.newCardsAvailable > 0 ? 'cursor-pointer' : 'opacity-50 cursor-default'"
          @click="srsStore.dueSummary!.newCardsAvailable > 0 && startStudy()"
        >
          <span
            class="text-2xl font-bold tabular-nums"
            :class="srsStore.dueSummary.newCardsAvailable > 0 ? 'text-green-600 dark:text-green-400' : 'text-gray-400 dark:text-gray-500'"
          >{{ srsStore.dueSummary.newCardsAvailable }}</span>
          <span class="text-xs text-gray-500 dark:text-gray-400">New</span>
        </button>

        <div class="flex items-center justify-center gap-2 py-3 px-3">
          <span
            class="text-2xl font-bold tabular-nums"
            :class="srsStore.dueSummary.reviewsToday > 0 ? 'text-purple-600 dark:text-purple-400' : 'text-gray-400 dark:text-gray-500'"
          >{{ srsStore.dueSummary.reviewsToday }}</span>
          <span class="text-xs text-gray-500 dark:text-gray-400">Done Today</span>
        </div>

        <div class="flex items-center justify-center gap-2 py-3 px-3">
          <template v-if="totalDue === 0 && nextReviewText">
            <span class="text-base font-bold tabular-nums text-gray-600 dark:text-gray-300">{{ nextReviewText }}</span>
            <span class="text-xs text-gray-500 dark:text-gray-400">Next Review</span>
          </template>
          <template v-else>
            <span
              class="text-2xl font-bold tabular-nums"
              :class="totalDue > 0 ? 'text-orange-600 dark:text-orange-400' : 'text-gray-400 dark:text-gray-500'"
            >{{ totalDue }}</span>
            <span class="text-xs text-gray-500 dark:text-gray-400">Total</span>
          </template>
        </div>
      </div>
    </div>

    <!-- Streak & Mini Heatmap -->
    <div
      v-if="!loading && srsStore.deckStreak && srsStore.deckStreak.totalReviewDays > 0 && srsStore.studyDecks.length > 0"
      class="mb-6 rounded-xl border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-800 shadow-sm p-4"
    >
      <div class="flex flex-wrap items-center gap-x-5 gap-y-3">
        <!-- Streak -->
        <div class="flex items-center gap-2">
          <Icon name="material-symbols:local-fire-department" size="1.5rem" class="text-orange-500" />
          <span class="text-xl font-bold tabular-nums">{{ srsStore.deckStreak.currentStreak }}</span>
          <span class="text-sm text-gray-500">day streak</span>
        </div>
        <div v-if="srsStore.deckStreak.isNewRecord && srsStore.deckStreak.currentStreak > 1" class="text-xs font-semibold text-orange-500">
          New record!
        </div>
        <div class="text-sm text-gray-500">
          Longest: <span class="font-semibold text-gray-700 dark:text-gray-300 tabular-nums">{{ srsStore.deckStreak.longestStreak }}</span>
        </div>
        <div class="text-sm text-gray-500">
          Days studied: <span class="font-semibold text-gray-700 dark:text-gray-300 tabular-nums">{{ srsStore.deckStreak.totalReviewDays }}</span>
        </div>
      </div>

      <!-- Mini Heatmap -->
      <div v-if="miniHeatmap.length > 0" class="mt-3 overflow-x-auto flex flex-col items-start">
        <div class="text-xs text-gray-500 mb-1">Review activity</div>
        <div class="relative" :style="{ width: `${WEEKS * (CELL + GAP) - GAP}px`, height: `${7 * (CELL + GAP) - GAP}px` }">
          <div
            v-for="(day, i) in miniHeatmap"
            :key="i"
            class="absolute rounded-sm"
            :class="miniIntensity(day.count)"
            :style="{
              left: `${day.weekIdx * (CELL + GAP)}px`,
              top: `${day.dow * (CELL + GAP)}px`,
              width: `${CELL}px`,
              height: `${CELL}px`,
            }"
            :title="`${day.date}: ${day.count} reviews`"
          />
        </div>
      </div>
    </div>

    <!-- Loading -->
    <div v-if="loading" class="flex justify-center py-12">
      <ProgressSpinner style="width: 40px; height: 40px" />
    </div>

    <!-- Error state -->
    <div v-else-if="srsStore.fetchError" class="text-center py-16">
      <div class="text-red-400 text-lg mb-4">{{ srsStore.fetchError }}</div>
      <Button icon="pi pi-refresh" label="Retry" @click="srsStore.fetchStudyDecks()" />
    </div>

    <!-- Empty state -->
    <div v-else-if="srsStore.studyDecks.length === 0" class="text-center py-16">
      <div class="text-gray-400 text-lg mb-4">No study decks yet</div>
      <p class="text-gray-500 mb-6">Add media decks to start learning vocabulary with spaced repetition.</p>
      <Button icon="pi pi-plus" label="Add Your First Deck" @click="showAddDialog = true" />
    </div>

    <!-- Deck list -->
    <div v-else ref="deckListRef" class="flex flex-col gap-3">
      <div
        v-for="(deck, index) in srsStore.studyDecks"
        :key="deck.userStudyDeckId"
        class="flex items-center gap-4 p-4 bg-white dark:bg-gray-800 rounded-xl shadow-sm border border-gray-200 dark:border-gray-700 transition-opacity"
        :class="{
          'opacity-50': dragIndex === index,
          'border-purple-400 dark:border-purple-500': dropIndex === index && dragIndex !== index,
        }"
      >
        <!-- Drag handle -->
        <div
          v-if="srsStore.studyDecks.length > 1"
          class="flex-shrink-0 cursor-grab active:cursor-grabbing text-gray-400 hover:text-gray-600 dark:hover:text-gray-300"
          style="touch-action: none"
          @pointerdown="handlePointerDown($event, index)"
        >
          <Icon name="material-symbols:drag-indicator" size="20" />
        </div>

        <!-- Cover -->
        <div class="w-16 h-20 flex-shrink-0 rounded overflow-hidden bg-gray-100 dark:bg-gray-700">
          <img
            v-if="getCoverUrl(deck.coverName)"
            :src="getCoverUrl(deck.coverName)!"
            :alt="deck.title"
            class="w-full h-full object-cover"
          />
          <div v-else class="w-full h-full flex items-center justify-center text-gray-400">
            <Icon name="material-symbols:book-2" size="24" />
          </div>
        </div>

        <!-- Info -->
        <div class="flex-1 min-w-0">
          <div class="font-semibold truncate">{{ localiseTitle({ originalTitle: deck.title, romajiTitle: deck.romajiTitle, englishTitle: deck.englishTitle }) }}</div>
          <div class="text-sm text-gray-500">
            {{ getMediaTypeText(deck.mediaType) }}
            <span v-if="deck.totalWords"> · {{ deck.totalWords }} words</span>
          </div>
          <div v-if="deck.totalWords > 0" class="mt-2">
            <div class="relative w-full bg-gray-200 dark:bg-gray-700 rounded-lg h-6 overflow-hidden">
              <div class="absolute bg-purple-500/40 h-6 rounded-lg transition-all duration-700" :style="{ width: combinedPct(deck) + '%' }" />
              <div class="absolute bg-purple-500 h-6 rounded-lg transition-all duration-700" :style="{ width: knownPct(deck) + '%' }" />
              <span class="absolute inset-0 flex items-center pl-2 text-xs font-bold z-10 text-white drop-shadow-[0_0_2px_rgba(0,0,0,0.6)]">
                {{ knownPct(deck) }}%
              </span>
            </div>
            <div class="flex gap-3 mt-1 text-xs text-gray-500 flex-wrap">
              <span>{{ deck.unseenCount }} unseen</span>
              <span class="text-purple-400">{{ deck.learningCount }} learning</span>
              <span class="text-purple-600">{{ deck.reviewCount + deck.masteredCount }} known</span>
              <span v-if="deck.dueReviewCount > 0" class="text-blue-500 font-semibold">{{ deck.dueReviewCount }} due</span>
            </div>
          </div>
        </div>

        <!-- Actions -->
        <div class="flex gap-1 flex-shrink-0">
          <div v-if="srsStore.studyDecks.length > 1" class="flex flex-col">
            <Button
              icon="pi pi-chevron-up"
              text
              size="small"
              :disabled="index === 0"
              @click="moveDeck(index, -1)"
            />
            <Button
              icon="pi pi-chevron-down"
              text
              size="small"
              :disabled="index === srsStore.studyDecks.length - 1"
              @click="moveDeck(index, 1)"
            />
          </div>
          <Button
            icon="pi pi-pencil"
            severity="secondary"
            text
            size="small"
            @click="openEdit(deck)"
          />
          <Button
            icon="pi pi-trash"
            severity="danger"
            text
            size="small"
            @click="confirmRemove(deck.userStudyDeckId, deck.title)"
          />
        </div>
      </div>
    </div>

    <SrsAddDeckDialog v-model:visible="showAddDialog" />
    <SrsAddDeckDialog v-model:visible="showEditDialog" :edit-deck="editingDeck" />
  </div>
</template>
