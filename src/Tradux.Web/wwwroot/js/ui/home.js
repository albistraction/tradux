// Tela inicial (Fase 22, ADR-0025): os templates do serviço, a criação de um
// novo DE-PARA e como o integrador converte. Só h()/textContent.

import { h, dateTime } from './dom.js';

/**
 * props: { hasCredential, templates (null = carregando), templatesError, homeError, busy,
 *          current ({ templateId, isNew, baseVersion }|null), newTemplateName,
 *          onNewTemplateName(value), onCreate(), onOpen(templateId, version), onContinue() }
 */
export function homeView({
  hasCredential, templates, templatesError, homeError, busy, current, newTemplateName,
  onNewTemplateName, onCreate, onOpen, onContinue,
}) {
  const list = !hasCredential
    ? h('p', { class: 'muted' }, 'Informe a credencial da API (topo da página) para ver os templates.')
    : templatesError
      ? h('p', { class: 'banner error', role: 'alert' }, templatesError)
      : templates === null
        ? h('p', { class: 'muted' }, 'Carregando…')
        : templates.length === 0
          ? h('p', { class: 'muted', id: 'no-templates' }, 'Nenhum template publicado ainda. Crie o primeiro ao lado.')
          : h('table', { id: 'templates-table' },
            h('thead', {}, h('tr', {}, h('th', {}, 'Template'), h('th', {}, 'Versão ativa'), h('th', {}, 'Publicada em'), h('th', {}, ''))),
            h('tbody', {}, templates.map((t) => h('tr', {},
              h('td', {}, t.templateId, t.operation !== 'default' ? h('span', { class: 'muted' }, ` (${t.operation})`) : null),
              h('td', {}, `v${t.activeVersion}`),
              h('td', {}, dateTime(t.publishedAtUtc)),
              h('td', {}, t.operation === 'default' && h('button', {
                type: 'button', class: 'link', disabled: busy, 'data-template': t.templateId,
                onclick: () => onOpen(t.templateId, t.activeVersion),
              }, 'abrir'))))));

  return [
    h('section', { class: 'card' },
      h('h2', { id: 'home-title' }, 'Templates de conversão'),
      h('p', { class: 'hint' }, 'Cada template traduz um XML no formato do XSD de origem para o XML do XSD de destino. O serviço guarda as versões: abra um template para publicar a próxima versão ou reativar uma anterior.'),
      list,
      homeError && h('p', { class: 'banner error', role: 'alert' }, homeError)),
    h('section', { class: 'card' },
      h('h2', {}, 'Novo template'),
      h('form', {
        id: 'new-template-form',
        onsubmit: (e) => { e.preventDefault(); onCreate(); },
      },
      h('label', { for: 'new-template-name' }, 'Nome do template'),
      h('input', {
        id: 'new-template-name', type: 'text', spellcheck: 'false', placeholder: 'dps-municipio', value: newTemplateName,
        oninput: (e) => onNewTemplateName(e.target.value),
      }),
      h('button', { type: 'submit', class: 'primary', id: 'btn-new-template' }, 'Criar DE-PARA')),
      current && h('p', {},
        h('button', { type: 'button', class: 'link', id: 'btn-continue', onclick: onContinue },
          `Continuar a edição de "${current.templateId}"`),
        h('span', { class: 'muted' }, current.isNew ? ' (ainda não publicado)' : ` (a partir da v${current.baseVersion}, não publicada)`)),
      h('h2', {}, 'Como converter'),
      h('p', { class: 'hint' }, 'Envie o XML de origem para o template; a resposta é o XML convertido pela versão ativa:'),
      h('pre', {}, 'POST /v1/templates/{nome}/convert\nAuthorization: ApiKey <credencial com template.use>\nContent-Type: application/xml\n\n<xml de origem/>')),
  ];
}
