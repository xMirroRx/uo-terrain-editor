/**
* @file CServerMapEdits.h
*
* Runtime terrain edits (in-game terrain editor). Holds the 8x8 map blocks that
* have been modified at runtime; they override the data read from mapX.mul.
*/

#ifndef _INC_CSERVERMAPEDITS_H
#define _INC_CSERVERMAPEDITS_H

#include "../game/uo_files/CUOMapBlock.h"
#include "../game/uo_files/CUOMapList.h"
#include "common.h"
#include <unordered_map>


// Serpme katmanı: bir karenin zemininin ÜSTÜNE çizilen ek grafik (kum, çakıl, taş kırıntısı).
// Zeminin kendisini değiştirmez, altındaki döşeme görünmeye devam eder. 0 = bu karede serpinti yok.
struct CMapOverlayBlock
{
	word m_wTile[UO_BLOCK_SIZE * UO_BLOCK_SIZE];
};

class CServerMapEdits
{
	// Patched terrain blocks, indexed by map and by block index (the same index used in mapX.mul).
	std::unordered_map<dword, CUOMapBlock> _blocks[MAP_SUPPORTED_QTY];
	// The untouched mul contents of every block we edited, kept so a block can be restored.
	std::unordered_map<dword, CUOMapBlock> _originals[MAP_SUPPORTED_QTY];
	// Serpme katmanı blokları (zeminin üstüne çizilen kum/çakıl), aynı blok dizini ile.
	std::unordered_map<dword, CMapOverlayBlock> _overlays[MAP_SUPPORTED_QTY];
	bool _fDirty;

public:
	static const char* m_sClassName;

	CServerMapEdits() noexcept;
	~CServerMapEdits() = default;

	CServerMapEdits(const CServerMapEdits& copy) = delete;
	CServerMapEdits& operator=(const CServerMapEdits& other) = delete;

	// Returns the patched block, or nullptr if this block was never edited.
	const CUOMapBlock* GetBlock(int iMap, dword dwBlockIdx) const noexcept;

	// The block as it was in the mul files, before the first edit: what RESET restores.
	const CUOMapBlock* GetOriginalBlock(int iMap, dword dwBlockIdx) const noexcept;
	void SetOriginalBlock(int iMap, dword dwBlockIdx, const CUOMapBlock& block);

	// Stores (or replaces) a patched block. Does NOT touch the block cache: the caller must invalidate it.
	void SetBlock(int iMap, dword dwBlockIdx, const CUOMapBlock& block);

	// Every edited block of a map, so they can be pushed to a client that just connected.
	inline const std::unordered_map<dword, CUOMapBlock>* GetBlocks(int iMap) const noexcept {
		return ((iMap >= 0) && (iMap < MAP_SUPPORTED_QTY)) ? &(_blocks[iMap]) : nullptr;
	}

	// ----- serpme katmanı -----
	// Zeminin üstüne çizilen ek grafikler. Harita bloklarıyla aynı dizinle saklanır ama mul verisine
	// karışmaz: istemciye ayrı bir paketle gider ve zemini hiç değiştirmez.
	const CMapOverlayBlock* GetOverlayBlock(int iMap, dword dwBlockIdx) const noexcept;
	word GetOverlay(int iMap, dword dwBlockIdx, int iIdx) const noexcept;
	void SetOverlay(int iMap, dword dwBlockIdx, int iIdx, word wTile);

	inline const std::unordered_map<dword, CMapOverlayBlock>* GetOverlayBlocks(int iMap) const noexcept {
		return ((iMap >= 0) && (iMap < MAP_SUPPORTED_QTY)) ? &(_overlays[iMap]) : nullptr;
	}

	inline bool HasEdits(int iMap) const noexcept {
		return ((iMap >= 0) && (iMap < MAP_SUPPORTED_QTY) && !_blocks[iMap].empty());
	}
	size_t GetEditedBlockCount() const noexcept;

	inline bool IsDirty() const noexcept {
		return _fDirty;
	}

	void Clear() noexcept;

	// Persistence. The file lives in the world save folder.
	bool Save(lpctstr ptcFilename);
	bool Load(lpctstr ptcFilename);
};

extern CServerMapEdits g_MapEdits;

#endif // _INC_CSERVERMAPEDITS_H
