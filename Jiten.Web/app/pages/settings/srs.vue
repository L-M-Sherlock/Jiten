<script setup lang="ts">
  import SettingsSrsStudy from '~/components/SettingsSrsStudy.vue';
  import SettingsFsrsParameters from '~/components/SettingsFsrsParameters.vue';

  definePageMeta({
    middleware: ['auth'],
  });

  useHead({ title: 'SRS Settings' });
</script>

<template>
  <div class="container mx-auto p-2 md:p-4">
    <SettingsSrsStudy class="mb-4" />
    <SettingsFsrsParameters />
  </div>
</template>
