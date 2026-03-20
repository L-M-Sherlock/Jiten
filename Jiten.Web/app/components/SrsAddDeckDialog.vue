<script setup lang="ts">
  import { type Deck, type MediaSuggestion, type StudyDeckDto, DeckDownloadType, DeckOrder, MediaType } from '~/types';
  import { useSrsStore } from '~/stores/srsStore';
  import { useToast } from 'primevue/usetoast';
  import { debounce } from 'perfect-debounce';

  const props = defineProps<{
    visible: boolean;
    preselectedDeck?: Deck;
    editDeck?: StudyDeckDto;
  }>();

  const emit = defineEmits(['update:visible']);
  const { $api } = useNuxtApp();
  const srsStore = useSrsStore();
  const toast = useToast();
  const localiseTitle = useLocaliseTitle();

  const localVisible = ref(props.visible);
  watch(() => props.visible, (v) => { localVisible.value = v; });
  watch(localVisible, (v) => { emit('update:visible', v); });

  const isEditMode = computed(() => !!props.editDeck);

  const step = ref(props.preselectedDeck || props.editDeck ? 2 : 1);
  const selectedDeck = ref<{ deckId: number; title: string; coverName?: string } | null>(
    props.editDeck
      ? { deckId: props.editDeck.deckId, title: props.editDeck.title, coverName: props.editDeck.coverName }
      : props.preselectedDeck
        ? { deckId: props.preselectedDeck.deckId, title: props.preselectedDeck.originalTitle, coverName: props.preselectedDeck.coverName ?? undefined }
        : null,
  );

  watch(() => props.preselectedDeck, (deck) => {
    if (deck) {
      selectedDeck.value = { deckId: deck.deckId, title: deck.originalTitle, coverName: deck.coverName ?? undefined };
      step.value = 2;
    }
  });

  function modeFromDownloadType(dt: number): Mode {
    if (dt === DeckDownloadType.TargetCoverage) return 'target';
    if (dt === DeckDownloadType.OccurrenceCount) return 'occurrence';
    return 'manual';
  }

  watch(() => props.editDeck, (deck) => {
    if (deck) {
      selectedDeck.value = { deckId: deck.deckId, title: deck.title, coverName: deck.coverName };
      downloadMode.value = modeFromDownloadType(deck.downloadType);
      downloadType.value = [DeckDownloadType.Full, DeckDownloadType.TopGlobalFrequency, DeckDownloadType.TopDeckFrequency, DeckDownloadType.TopChronological].includes(deck.downloadType)
        ? deck.downloadType
        : DeckDownloadType.TopGlobalFrequency;
      deckOrder.value = deck.order;
      minFrequency.value = deck.minFrequency;
      maxFrequency.value = deck.maxFrequency;
      targetPercentage.value = deck.targetPercentage ?? 80;
      minOccurrences.value = deck.minOccurrences;
      maxOccurrences.value = deck.maxOccurrences;
      excludeKana.value = deck.excludeKana;
      excludeMatureMasteredBlacklisted.value = deck.excludeMatureMasteredBlacklisted;
      excludeAllTrackedWords.value = deck.excludeAllTrackedWords;
      step.value = 2;
    }
  });

  // Step 1: Search
  const searchQuery = ref('');
  const searchResults = ref<MediaSuggestion[]>([]);
  const searching = ref(false);

  const debouncedSearch = debounce(async (query: string) => {
    if (query.length < 2) { searchResults.value = []; return; }
    searching.value = true;
    try {
      const response = await $api<{ suggestions: MediaSuggestion[] }>('media-deck/search-suggestions', {
        query: { query, limit: 10 },
      });
      searchResults.value = response.suggestions ?? [];
    } catch {
      searchResults.value = [];
    } finally {
      searching.value = false;
    }
  }, 300);

  watch(searchQuery, (q) => debouncedSearch(q));

  function selectDeck(suggestion: MediaSuggestion) {
    selectedDeck.value = { deckId: suggestion.deckId, title: suggestion.originalTitle, coverName: suggestion.coverName };
    step.value = 2;
  }

  // Step 2: Filters
  type Mode = 'manual' | 'target' | 'occurrence';
  const downloadMode = ref<Mode>('manual');
  const downloadType = ref(DeckDownloadType.TopGlobalFrequency);
  const deckOrder = ref(DeckOrder.GlobalFrequency);
  const minFrequency = ref(0);
  const maxFrequency = ref(30000);
  const targetPercentage = ref(80);
  const minOccurrences = ref<number | undefined>(undefined);
  const maxOccurrences = ref<number | undefined>(undefined);
  const excludeKana = ref(false);
  const excludeMatureMasteredBlacklisted = ref(true);
  const excludeAllTrackedWords = ref(false);
  const adding = ref(false);
  const previewCount = ref<number | null>(null);
  const isCountLoading = ref(false);

  const computedDownloadType = computed(() => {
    if (downloadMode.value === 'target') return DeckDownloadType.TargetCoverage;
    if (downloadMode.value === 'occurrence') return DeckDownloadType.OccurrenceCount;
    return downloadType.value;
  });

  let countRequestId = 0;
  const fetchPreviewCount = async () => {
    if (!selectedDeck.value || step.value !== 2) return;
    const reqId = ++countRequestId;
    isCountLoading.value = true;
    try {
      const response = await $api<number>('srs/study-decks/preview-count', {
        method: 'POST',
        body: {
          deckId: selectedDeck.value.deckId,
          downloadType: computedDownloadType.value,
          order: deckOrder.value,
          minFrequency: minFrequency.value,
          maxFrequency: maxFrequency.value,
          targetPercentage: downloadMode.value === 'target' ? targetPercentage.value : undefined,
          minOccurrences: downloadMode.value === 'occurrence' ? minOccurrences.value : undefined,
          maxOccurrences: downloadMode.value === 'occurrence' ? maxOccurrences.value : undefined,
          excludeKana: excludeKana.value,
          excludeMatureMasteredBlacklisted: excludeMatureMasteredBlacklisted.value,
          excludeAllTrackedWords: excludeAllTrackedWords.value,
        },
      });
      if (reqId === countRequestId && typeof response === 'number') {
        previewCount.value = response;
      }
    } catch {
      if (reqId === countRequestId) previewCount.value = null;
    } finally {
      if (reqId === countRequestId) isCountLoading.value = false;
    }
  };
  const fetchPreviewCountDebounced = debounce(fetchPreviewCount, 500);

  watch(
    [
      step, () => selectedDeck.value?.deckId,
      computedDownloadType, downloadType, deckOrder,
      minFrequency, maxFrequency, targetPercentage,
      minOccurrences, maxOccurrences,
      excludeKana, excludeMatureMasteredBlacklisted, excludeAllTrackedWords,
    ],
    () => {
      if (step.value !== 2 || !selectedDeck.value) {
        previewCount.value = null;
        return;
      }
      fetchPreviewCountDebounced();
    },
  );

  watch(step, (s) => {
    if (s === 2 && selectedDeck.value) fetchPreviewCount();
  });

  async function addDeck() {
    if (!selectedDeck.value) return;
    adding.value = true;
    try {
      const filterPayload = {
        downloadType: computedDownloadType.value,
        order: deckOrder.value,
        minFrequency: minFrequency.value,
        maxFrequency: maxFrequency.value,
        targetPercentage: downloadMode.value === 'target' ? targetPercentage.value : undefined,
        minOccurrences: downloadMode.value === 'occurrence' ? minOccurrences.value : undefined,
        maxOccurrences: downloadMode.value === 'occurrence' ? maxOccurrences.value : undefined,
        excludeKana: excludeKana.value,
        excludeMatureMasteredBlacklisted: excludeMatureMasteredBlacklisted.value,
        excludeAllTrackedWords: excludeAllTrackedWords.value,
      };

      if (isEditMode.value && props.editDeck) {
        await srsStore.updateStudyDeck(props.editDeck.userStudyDeckId, filterPayload);
        toast.add({ severity: 'success', summary: 'Deck filters updated', life: 3000 });
      } else {
        await srsStore.addStudyDeck({ deckId: selectedDeck.value.deckId, ...filterPayload });
        toast.add({ severity: 'success', summary: 'Deck added to study list', life: 3000 });
      }
      localVisible.value = false;
      resetForm();
    } catch (error: any) {
      const message = error?.data?.message || error?.data || (isEditMode.value ? 'Failed to update deck' : 'Failed to add deck');
      toast.add({ severity: 'error', summary: 'Error', detail: String(message), life: 5000 });
    } finally {
      adding.value = false;
    }
  }

  function resetForm() {
    step.value = (props.preselectedDeck || props.editDeck) ? 2 : 1;
    searchQuery.value = '';
    searchResults.value = [];
    if (!props.preselectedDeck && !props.editDeck) selectedDeck.value = null;
    downloadMode.value = 'manual';
    downloadType.value = DeckDownloadType.TopGlobalFrequency;
    deckOrder.value = DeckOrder.GlobalFrequency;
    minFrequency.value = 0;
    maxFrequency.value = 30000;
    targetPercentage.value = 80;
    minOccurrences.value = undefined;
    maxOccurrences.value = undefined;
    excludeKana.value = false;
    excludeMatureMasteredBlacklisted.value = true;
    excludeAllTrackedWords.value = false;
    previewCount.value = null;
  }

  function goBack() {
    if (props.preselectedDeck) {
      localVisible.value = false;
    } else {
      step.value = 1;
      selectedDeck.value = null;
    }
  }

  const downloadTypeOptions = [
    { label: 'Full', value: DeckDownloadType.Full },
    { label: 'Top Global Frequency', value: DeckDownloadType.TopGlobalFrequency },
    { label: 'Top Deck Frequency', value: DeckDownloadType.TopDeckFrequency },
    { label: 'Top Chronological', value: DeckDownloadType.TopChronological },
  ];

  const orderOptions = [
    { label: 'Chronological', value: DeckOrder.Chronological },
    { label: 'Global Frequency', value: DeckOrder.GlobalFrequency },
    { label: 'Deck Frequency', value: DeckOrder.DeckFrequency },
  ];

  const modeOptions = [
    { label: 'Manual Range', value: 'manual' },
    { label: 'Target Coverage', value: 'target' },
    { label: 'Occurrence Count', value: 'occurrence' },
  ];
</script>

<template>
  <Dialog
    v-model:visible="localVisible"
    :header="isEditMode ? 'Edit Study Deck' : 'Add Study Deck'"
    modal
    :style="{ width: '500px', maxWidth: '95vw' }"
    :pt="{ content: { class: 'p-4' } }"
  >
    <!-- Step 1: Search -->
    <div v-if="step === 1">
      <div class="mb-4">
        <label class="block text-sm font-medium mb-1">Search for a deck</label>
        <InputText v-model="searchQuery" placeholder="Type to search..." class="w-full" autofocus />
      </div>

      <div v-if="searching" class="flex justify-center py-4">
        <ProgressSpinner style="width: 30px; height: 30px" />
      </div>

      <div v-else class="flex flex-col gap-1 max-h-[400px] overflow-y-auto" role="listbox" aria-label="Search results">
        <div
          v-for="result in searchResults"
          :key="result.deckId"
          role="option"
          tabindex="0"
          class="flex items-center gap-3 p-2 rounded cursor-pointer hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors"
          @click="selectDeck(result)"
          @keydown.enter="selectDeck(result)"
        >
          <img
            :src="result.coverName && result.coverName !== 'nocover.jpg' ? result.coverName : '/img/nocover.jpg'"
            :alt="localiseTitle(result)"
            class="w-10 h-14 object-cover rounded shrink-0"
          />
          <div class="flex-1 min-w-0">
            <div class="text-sm font-medium truncate">{{ localiseTitle(result) }}</div>
            <div class="text-xs text-gray-500">{{ getMediaTypeText(result.mediaType) }}</div>
          </div>
        </div>
        <div v-if="searchQuery.length >= 2 && searchResults.length === 0 && !searching" class="text-center text-sm text-gray-500 py-4">
          No results found
        </div>
      </div>
    </div>

    <!-- Step 2: Configure filters -->
    <div v-if="step === 2 && selectedDeck">
      <div class="flex items-center gap-2 mb-4 pb-3 border-b border-gray-200 dark:border-gray-700">
        <Button v-if="!preselectedDeck && !isEditMode" icon="pi pi-arrow-left" severity="secondary" text size="small" @click="goBack" />
        <span class="font-semibold">{{ localiseTitle(selectedDeck) }}</span>
      </div>

      <!-- Mode selection -->
      <div class="mb-4">
        <label class="block text-sm font-medium mb-1">Filter Mode</label>
        <SelectButton v-model="downloadMode" :options="modeOptions" option-label="label" option-value="value" class="w-full" />
      </div>

      <!-- Manual range -->
      <template v-if="downloadMode === 'manual'">
        <div class="mb-3">
          <label class="block text-sm font-medium mb-1">Download Type</label>
          <Select v-model="downloadType" :options="downloadTypeOptions" option-label="label" option-value="value" class="w-full" />
        </div>
        <div v-if="downloadType !== DeckDownloadType.Full" class="grid grid-cols-2 gap-3 mb-3">
          <div>
            <label class="block text-xs mb-1">Min</label>
            <InputNumber v-model="minFrequency" :min="0" class="w-full" />
          </div>
          <div>
            <label class="block text-xs mb-1">Max</label>
            <InputNumber v-model="maxFrequency" :min="0" class="w-full" />
          </div>
        </div>
      </template>

      <!-- Target coverage -->
      <template v-if="downloadMode === 'target'">
        <div class="mb-3">
          <label class="block text-sm font-medium mb-1">Target Coverage: {{ targetPercentage }}%</label>
          <Slider v-model="targetPercentage" :min="1" :max="100" class="w-full" />
        </div>
      </template>

      <!-- Occurrence count -->
      <template v-if="downloadMode === 'occurrence'">
        <div class="grid grid-cols-2 gap-3 mb-3">
          <div>
            <label class="block text-xs mb-1">Min Occurrences</label>
            <InputNumber v-model="minOccurrences" :min="1" class="w-full" />
          </div>
          <div>
            <label class="block text-xs mb-1">Max Occurrences</label>
            <InputNumber v-model="maxOccurrences" :min="1" class="w-full" />
          </div>
        </div>
      </template>

      <!-- Order -->
      <div class="mb-3">
        <label class="block text-sm font-medium mb-1">Card Order</label>
        <Select v-model="deckOrder" :options="orderOptions" option-label="label" option-value="value" class="w-full" />
      </div>

      <!-- Exclusions -->
      <div class="flex flex-col gap-2 mb-4">
        <div class="flex items-center gap-2">
          <Checkbox v-model="excludeKana" input-id="excludeKana" :binary="true" />
          <label for="excludeKana" class="text-sm cursor-pointer">Exclude kana-only words</label>
        </div>
        <div class="flex items-center gap-2">
          <Checkbox v-model="excludeMatureMasteredBlacklisted" input-id="excludeKnown" :binary="true" />
          <label for="excludeKnown" class="text-sm cursor-pointer">Exclude mature/mastered/blacklisted</label>
        </div>
        <div class="flex items-center gap-2">
          <Checkbox v-model="excludeAllTrackedWords" input-id="excludeTracked" :binary="true" />
          <label for="excludeTracked" class="text-sm cursor-pointer">Exclude all tracked words</label>
        </div>
      </div>

      <div class="flex items-center justify-between mb-3">
        <span class="text-sm text-gray-600 dark:text-gray-300 inline-flex items-center gap-2">
          <template v-if="isCountLoading">
            <i class="pi pi-spin pi-spinner text-gray-400 dark:text-gray-500 text-xs" />
            <span>Counting...</span>
          </template>
          <template v-else-if="previewCount !== null">
            <span>~<span class="font-bold text-gray-900 dark:text-gray-100">{{ previewCount.toLocaleString() }}</span> words match</span>
          </template>
        </span>
      </div>
      <Button :label="isEditMode ? 'Save Changes' : 'Add to Study List'" class="w-full" :loading="adding" @click="addDeck" />
    </div>
  </Dialog>
</template>
