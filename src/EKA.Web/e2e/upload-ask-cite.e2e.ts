import { Page, expect, test } from '@playwright/test';

/**
 * Step 13's exit criterion, end to end on the real stack: a user uploads a document, waits for
 * indexing, asks questions, and inspects citations.
 *
 * The document carries a fact no model knows (a made-up allowance and a unique token), so a
 * correct, cited answer can only have come from retrieval over this upload.
 */

const password = 'local-dev-only'; // the local realm's shared test password (PROGRESS: known limitation)

async function signIn(page: Page, user: string): Promise<void> {
  // The guard sends us to Keycloak; we come back through /auth/callback with a code (PKCE).
  await page.getByRole('textbox', { name: 'Username or email' }).fill(user);
  await page.getByRole('textbox', { name: 'Password', exact: true }).fill(password);
  await page.getByRole('button', { name: 'Sign In', exact: true }).click();
}

test('upload, wait for indexing, ask, and inspect the citation', async ({ page }) => {
  const token = crypto.randomUUID().slice(0, 8);
  const amount = 30 + Math.floor(Math.random() * 60);
  const title = `Cycling allowance ${token}`;
  const markdown = [
    `# Cycling allowance`,
    ``,
    `## Amount`,
    ``,
    `Employees who cycle to work receive a monthly cycling allowance of ${amount} euros, paid with the salary.`,
    `Reference code ${token}.`,
    ``,
    `## Eligibility`,
    ``,
    `The allowance applies to employees who cycle at least three days a week.`,
  ].join('\n');

  await page.goto('/documents');
  await signIn(page, 'carol');
  await expect(page.getByRole('heading', { name: 'Documents', level: 1 })).toBeVisible();

  // ── Upload ──────────────────────────────────────────────────────────────────────
  await page.getByLabel('Title (optional)').fill(title);
  await page.getByTestId('file-input').setInputFiles({
    name: `cycling-${token}.md`,
    mimeType: 'text/markdown',
    buffer: Buffer.from(markdown, 'utf8'),
  });
  await expect(page.getByText(`"${title}" was uploaded and is being indexed.`).first()).toBeVisible();

  // ── Wait for indexing (the page polls while anything is in progress) ───────────
  const row = page.getByRole('row').filter({ hasText: title });
  await expect(row.getByText('Ready')).toBeVisible({ timeout: 120_000 });

  // ── Ask ─────────────────────────────────────────────────────────────────────────
  await page.getByRole('link', { name: 'Ask' }).click();
  await page.getByRole('button', { name: 'New conversation' }).click();
  await page.getByTestId('composer').fill(`How much is the monthly cycling allowance? (reference ${token})`);
  await page.getByRole('button', { name: 'Send' }).click();

  const answer = page.getByTestId('answer').last();
  await expect(answer).toBeVisible({ timeout: 60_000 });
  await expect(answer).toHaveAttribute('data-grounding', 'cited');
  await expect(answer).toContainText(String(amount));
  await expect(page).toHaveURL(/\/chat\?c=/);

  // ── Inspect the citation ───────────────────────────────────────────────────────
  const marker = answer.getByRole('button', { name: /^Source 1:/ });
  await expect(marker).toHaveAccessibleName(`Source 1: ${title}`);
  await marker.click();

  const citation = page.getByTestId('citation-1');
  await expect(citation).toContainText(title);
  await expect(citation).toContainText('Section: Cycling allowance');
  await expect(citation).toBeFocused();

  // ── A second question in the same conversation, still grounded ─────────────────
  await page.getByTestId('composer').fill('Who is eligible for it?');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.getByTestId('answer')).toHaveCount(2, { timeout: 60_000 });
  await expect(page.getByTestId('answer').last()).toContainText(/three days/i);

  // ── Reload: the conversation and its citations were persisted, not just shown ──
  await page.reload();
  await expect(page.getByTestId('answer')).toHaveCount(2);
  await expect(page.getByTestId('answer').first()).toHaveAttribute('data-grounding', 'cited');

  // ── Clean up ────────────────────────────────────────────────────────────────────
  await page.getByRole('link', { name: 'Documents' }).click();
  page.once('dialog', (dialog) => dialog.accept());
  await page.getByRole('button', { name: `Delete ${title}` }).click();
  await expect(page.getByRole('row').filter({ hasText: title })).toHaveCount(0);
});

test('a document restricted to another group never reaches a non-member’s answer', async ({ page }) => {
  // Authorization in retrieval, seen from the browser: dana (finance) uploads a finance-only fact;
  // carol (hr) asks for it and gets the no-evidence answer, with no model call and no citation.
  const token = crypto.randomUUID().slice(0, 8);
  const title = `Finance bonus ${token}`;

  await page.goto('/documents');
  await signIn(page, 'dana');
  await page.getByLabel('Title (optional)').fill(title);
  await page.getByTestId('file-input').setInputFiles({
    name: `bonus-${token}.md`,
    mimeType: 'text/markdown',
    buffer: Buffer.from(`# Finance bonus\n\nThe finance team bonus pool for code ${token} is 1.7 million euros.`, 'utf8'),
  });
  const row = page.getByRole('row').filter({ hasText: title });
  await row.getByRole('button', { name: `Change who can read ${title}` }).click();
  await page.getByRole('radio', { name: 'Only specific people and groups' }).check();
  await page.getByPlaceholder('group:hr or user:<id>').fill('group:finance');
  await page.getByPlaceholder('group:hr or user:<id>').press('Enter');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(row.getByText('Ready')).toBeVisible({ timeout: 120_000 });

  await page.getByRole('button', { name: 'Sign out' }).click();
  await page.goto('/chat');
  await signIn(page, 'carol');
  await page.getByTestId('composer').fill(`What is the finance team bonus pool for code ${token}?`);
  await page.getByRole('button', { name: 'Send' }).click();

  // Not asserting `no_evidence`: whether anything else carol can read clears the score floor depends
  // on what else the tenant holds (a cycling allowance is "near" a bonus). The property that matters
  // is that the restricted fact and the restricted document never reach her.
  const answer = page.getByTestId('answer').last();
  await expect(answer).toBeVisible({ timeout: 60_000 });
  await expect(answer).not.toHaveAttribute('data-grounding', 'cited');
  await expect(answer).not.toContainText('1.7');
  await expect(page.getByRole('complementary')).not.toContainText(title);

  // Clean up as the owner.
  await page.getByRole('button', { name: 'Sign out' }).click();
  await page.goto('/documents');
  await signIn(page, 'dana');
  page.once('dialog', (dialog) => dialog.accept());
  await page.getByRole('button', { name: `Delete ${title}` }).click();
  await expect(page.getByRole('row').filter({ hasText: title })).toHaveCount(0);
});
