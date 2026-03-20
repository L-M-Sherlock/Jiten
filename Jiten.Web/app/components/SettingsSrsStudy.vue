<script setup lang="ts">
  import { useSrsStore } from '~/stores/srsStore';
  import { useToast } from 'primevue/usetoast';

  const props = defineProps<{ inline?: boolean }>();

  const srsStore = useSrsStore();
  const toast = useToast();

  const form = reactive({ ...srsStore.studySettings });
  const saving = ref(false);
  const loaded = ref(false);

  onMounted(async () => {
    await srsStore.fetchSettings();
    Object.assign(form, srsStore.studySettings);
    loaded.value = true;
  });

  const gradingOptions = [
    { label: '4 buttons', value: 4 },
    { label: '2 buttons', value: 2 },
  ];

  const interleavingOptions = [
    { label: 'Mixed', value: 'Mixed' },
    { label: 'New first', value: 'NewFirst' },
    { label: 'Reviews first', value: 'ReviewsFirst' },
  ];

  const newCardOrderOptions = [
    { label: 'Deck frequency', value: 'DeckFrequency' },
    { label: 'Global frequency', value: 'GlobalFrequency' },
    { label: 'Random', value: 'Random' },
  ];

  const reviewFromOptions = [
    { label: 'All tracked', value: 'AllTracked' },
    { label: 'Study decks only', value: 'StudyDecksOnly' },
  ];

  const exampleSentenceOptions = [
    { label: 'Hidden', value: 'Hidden' },
    { label: 'Front', value: 'Front' },
    { label: 'Back', value: 'Back' },
  ];

  async function save() {
    saving.value = true;
    try {
      await srsStore.updateSettings({ ...form });
      toast.add({ severity: 'success', summary: 'Study settings saved', life: 2000 });
    } catch {
      toast.add({ severity: 'error', summary: 'Failed to save settings', life: 3000 });
    } finally {
      saving.value = false;
    }
  }

  const CardWrapper = defineComponent({
    props: { card: Boolean },
    setup(wrapperProps, { slots }) {
      return () => {
        if (!wrapperProps.card) return slots.default?.();
        return h(resolveComponent('Card'), null, {
          title: () => h('h3', { class: 'text-lg font-semibold' }, 'SRS Study'),
          content: () => slots.default?.(),
        });
      };
    },
  });
</script>

<template>
  <CardWrapper :card="!props.inline">
    <div v-if="!loaded" class="flex justify-center py-4">
      <ProgressSpinner style="width: 24px; height: 24px" />
    </div>
    <div v-else class="flex flex-col gap-4">
      <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
        <div>
          <label class="block text-sm font-medium mb-1">New cards per day</label>
          <InputNumber v-model="form.newCardsPerDay" :min="0" :max="9999" :show-buttons="!props.inline" class="w-full" />
        </div>
        <div>
          <label class="block text-sm font-medium mb-1">Max reviews per day</label>
          <InputNumber v-model="form.maxReviewsPerDay" :min="0" :max="9999" :show-buttons="!props.inline" class="w-full" />
        </div>
      </div>

      <div>
        <label class="block text-sm font-medium mb-1">Grading buttons</label>
        <SelectButton v-model="form.gradingButtons" :options="gradingOptions" option-label="label" option-value="value" />
      </div>

      <div>
        <label class="block text-sm font-medium mb-1">Card interleaving</label>
        <SelectButton v-model="form.interleaving" :options="interleavingOptions" option-label="label" option-value="value" class="flex-wrap" />
      </div>

      <div>
        <label class="block text-sm font-medium mb-1">New card order</label>
        <SelectButton v-model="form.newCardOrder" :options="newCardOrderOptions" option-label="label" option-value="value" class="flex-wrap" />
      </div>

      <div>
        <label class="block text-sm font-medium mb-1">Review cards from</label>
        <SelectButton v-model="form.reviewFrom" :options="reviewFromOptions" option-label="label" option-value="value" class="flex-wrap" />
      </div>

      <div>
        <label class="block text-sm font-medium mb-2">Card back content</label>
        <div class="flex flex-col gap-2">
          <div class="flex items-center gap-2">
            <ToggleSwitch v-model="form.showPitchAccent" input-id="showPitchAccent" />
            <label for="showPitchAccent" class="text-sm cursor-pointer">Pitch accent</label>
          </div>
          <div>
            <label class="text-sm mb-1 block">Example sentence</label>
            <SelectButton v-model="form.exampleSentencePosition" :options="exampleSentenceOptions" option-label="label" option-value="value" />
          </div>
          <div class="flex items-center gap-2">
            <ToggleSwitch v-model="form.showFrequencyRank" input-id="showFrequencyRank" />
            <label for="showFrequencyRank" class="text-sm cursor-pointer">Frequency rank</label>
          </div>
          <div class="flex items-center gap-2">
            <ToggleSwitch v-model="form.showKanjiBreakdown" input-id="showKanjiBreakdown" />
            <label for="showKanjiBreakdown" class="text-sm cursor-pointer">Kanji breakdown</label>
          </div>
        </div>
      </div>

      <div>
        <label class="block text-sm font-medium mb-2">Grade buttons</label>
        <div class="flex flex-col gap-2">
          <div class="flex items-center gap-2">
            <ToggleSwitch v-model="form.showNextInterval" input-id="showNextInterval" />
            <label for="showNextInterval" class="text-sm cursor-pointer">Show next interval on buttons</label>
          </div>
          <div class="flex items-center gap-2">
            <ToggleSwitch v-model="form.showKeybinds" input-id="showKeybinds" />
            <label for="showKeybinds" class="text-sm cursor-pointer">Show keyboard shortcuts</label>
          </div>
          <div class="flex items-center gap-2">
            <ToggleSwitch v-model="form.showElapsedTime" input-id="showElapsedTime" />
            <label for="showElapsedTime" class="text-sm cursor-pointer">Show elapsed time</label>
          </div>
        </div>
      </div>

      <Button label="Save" :loading="saving" class="w-full md:w-auto" @click="save" />
    </div>
  </CardWrapper>
</template>
