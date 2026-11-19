// Painel "Publicar" (Fase 22, ADR-0025): cadastra a próxima versão do template
// no serviço e mostra o histórico de versões (abrir uma versão, reativá-la).
// Só h()/textContent — nada de innerHTML.

import { h, dateTime } from './dom.js';
import { describePublication } from '../model/publish.js';
import { qualityReport } from './qualityPanel.js';

/**
 * props: { templateId, isNew, baseVersion, problems, result, error, busy, canPublish, versions, versionsError,
 *          onTemplateId(value), onPublish(), onOpenVersion(version), onRollback(version), onSelectPath(path) }
 */
export function publishPanel({
  templateId, isNew, baseVersion, problems, result, error, busy, canPublish, versions, versionsError,
  onTemplateId, onPublish, onOpenVersion, onRollback, onSelectPath,
}) {
  const active = versions?.find((v) => v.status === 'Active');
  const next = (versions?.length ? Math.max(...versions.map((v) => v.version)) : 0) + 1;
  const parts = [
    h('p', { class: 'hint' }, 'Cada publicação vira a próxima versão do template no serviço — só se compilação, testes estruturais e o cenário (XML de exemplo + XML esperado) passarem. A versão ativa é a que o microsserviço usa para converter.'),
    h('div', { class: 'publish-target' },
      h('label', { class: 'publish-field' }, 'Template',
        h('input', {
          type: 'text', id: 'publish-templateId', value: templateId ?? '', spellcheck: 'false', placeholder: 'dps-municipio',
          disabled: !isNew, oninput: (e) => onTemplateId(e.target.value),
        })),
      h('button', { type: 'button', class: 'primary', id: 'btn-publish', disabled: busy || !canPublish, onclick: onPublish },
        isNew ? 'Publicar versão 1' : `Publicar versão ${next}`)),
    !isNew && baseVersion != null && h('p', { class: 'muted', id: 'publish-base' }, `Editando a partir da versão ${baseVersion}.`),
  ];

  if (problems.length > 0) {
    parts.push(h('ul', { class: 'banner warning' }, problems.map((p) => h('li', {}, p))));
  }

  if (busy) parts.push(h('p', { class: 'muted' }, 'Aguarde…'));
  if (error) parts.push(h('p', { class: 'banner error', role: 'alert' }, error));

  const summary = describePublication(result);
  if (summary) {
    parts.push(h('div', { class: `banner ${summary.ok ? 'ok' : 'error'}`, role: 'status', id: 'publish-status' }, summary.ok ? `✔ ${summary.text}` : `✖ ${summary.text}`));
    if (!summary.ok && result.diagnostics?.length) {
      parts.push(h('ul', { class: 'diagnostics' }, result.diagnostics.slice(0, 20).map((d) =>
        h('li', { class: `diag severity-${d.severity}` }, h('code', {}, d.code), ' ', d.destinationXPath ?? '', h('div', {}, d.message)))));
    }

    if (result.report) {
      parts.push(h('div', { 'aria-live': 'polite' }, qualityReport({ report: result.report, passed: result.report.passed, onSelectPath })));
    }
  }

  if (!isNew) {
    parts.push(h('section', { class: 'versions', 'aria-labelledby': 'versions-title' },
      h('h3', { id: 'versions-title' }, 'Versões'),
      versionsError && h('p', { class: 'banner error', role: 'alert' }, versionsError),
      versions?.length > 0 && h('table', { id: 'versions-table' },
        h('thead', {}, h('tr', {}, h('th', {}, 'Versão'), h('th', {}, 'Publicada em'), h('th', {}, 'Situação'), h('th', {}, ''))),
        h('tbody', {}, versions.map((v) => h('tr', { class: v.status === 'Active' ? 'active' : null },
          h('td', {}, `v${v.version}`),
          h('td', {}, dateTime(v.publishedAtUtc)),
          h('td', {}, v.status === 'Active' ? 'ativa' : 'anterior'),
          h('td', {},
            h('button', { type: 'button', class: 'link', disabled: busy, onclick: () => onOpenVersion(v.version), 'aria-label': `Abrir a versão ${v.version} no editor` }, 'abrir'),
            v.status !== 'Active' && ' ',
            v.status !== 'Active' && h('button', {
              type: 'button', class: 'link', disabled: busy || !canPublish, onclick: () => onRollback(v.version),
              'aria-label': `Reativar a versão ${v.version}`,
            }, 'reativar')))))),
      active && h('p', { class: 'muted' }, `O microsserviço converte com a v${active.version}: POST /v1/templates/${templateId}/convert`)));
  }

  return h('div', { 'aria-live': 'polite' }, parts);
}
