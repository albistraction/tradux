// "Lente" sobre um pedaço do estado: { get(), set(valor) }. Os editores leem o
// valor ATUAL na hora do evento (lens.get()) em vez de um valor capturado na
// renderização — assim, um painel que não foi recriado enquanto o usuário
// digitava nunca grava de volta um valor antigo (revisão da Fase 6, A2).

export const prop = (parent, key) => ({
  get: () => parent.get()?.[key],
  set: (value) => parent.set({ ...(parent.get() ?? {}), [key]: value }),
});

export const item = (parent, index) => ({
  get: () => parent.get()?.[index],
  set: (value) => parent.set((parent.get() ?? []).map((v, i) => (i === index ? value : v))),
});
